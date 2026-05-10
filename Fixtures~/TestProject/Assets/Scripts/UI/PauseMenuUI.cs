using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TestProject.Systems;

namespace TestProject.UI
{
    public class PauseMenuUI : MonoBehaviour
    {
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button mainMenuButton;
        [SerializeField] private GameObject pausePanel;

        void Start()
        {
            resumeButton.onClick.AddListener(OnResume);
            mainMenuButton.onClick.AddListener(() => SceneManager.LoadScene("MainMenu"));
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (GameManager.Instance.CurrentState == GameState.Paused) OnResume();
                else { GameManager.Instance.PauseGame(); pausePanel.SetActive(true); }
            }
        }

        void OnResume() { GameManager.Instance.ResumeGame(); pausePanel.SetActive(false); }
    }
}
