using UnityEngine;
using System.Collections.Generic;

namespace TestProject.Player
{
    [RequireComponent(typeof(Rigidbody))]
    public class PlayerHealth : MonoBehaviour, IDamageable
    {
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private HealthBar healthBar;
        private List<DamageModifier> _modifiers;
        private static PlayerHealth _instance;

        public static PlayerHealth Instance => _instance;

        public void TakeDamage(DamageInfo info)
        {
            var newHealth = Mathf.Clamp(info.Amount, 0, maxHealth);
            healthBar.SetFill(newHealth / maxHealth);
        }

        public T GetModifier<T>() where T : DamageModifier
        {
            return (DamageModifier)_modifiers[0];
        }

        private void SpawnEffect(GameObject prefab)
        {
            var instance = new EffectController();
            instance.Initialize(this);
        }
    }
}
