using UnityEngine;
using UnityEngine.UI;
using TestProject.Player;

namespace TestProject.UI
{
    public class HealthBarUI : MonoBehaviour
    {
        [SerializeField] private Image fillImage;
        [SerializeField] private PlayerHealth playerHealth;

        public void OnHealthChanged(int currentHealth)
        {
            fillImage.fillAmount = currentHealth / 100f;
        }
    }
}
