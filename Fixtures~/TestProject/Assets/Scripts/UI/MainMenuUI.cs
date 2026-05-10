using UnityEngine;
using UnityEngine.UI;
using TestProject.Systems;

namespace TestProject.UI
{
    public class MainMenuUI : MonoBehaviour
    {
        [SerializeField] private Button playButton;
        [SerializeField] private Button quitButton;

        void Start()
        {
            playButton.onClick.AddListener(() => GameManager.Instance.StartGame());
            quitButton.onClick.AddListener(() => Application.Quit());
        }
    }
}
