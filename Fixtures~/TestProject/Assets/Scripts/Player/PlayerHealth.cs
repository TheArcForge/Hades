using UnityEngine;
using UnityEngine.Events;

namespace TestProject.Player
{
    public class PlayerHealth : MonoBehaviour
    {
        [SerializeField] private int maxHealth = 100;
        [SerializeField] private UnityEvent<int> onHealthChanged;
        [SerializeField] private UnityEvent onDeath;

        private int _currentHealth;
        public int CurrentHealth => _currentHealth;

        void Start() { _currentHealth = maxHealth; }

        public void TakeDamage(int amount)
        {
            _currentHealth = Mathf.Max(0, _currentHealth - amount);
            onHealthChanged?.Invoke(_currentHealth);
            if (_currentHealth <= 0) onDeath?.Invoke();
        }

        public void Heal(int amount)
        {
            _currentHealth = Mathf.Min(maxHealth, _currentHealth + amount);
            onHealthChanged?.Invoke(_currentHealth);
        }
    }
}
