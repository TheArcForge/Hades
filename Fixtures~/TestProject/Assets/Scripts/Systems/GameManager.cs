using UnityEngine;
using UnityEngine.SceneManagement;

namespace TestProject.Systems
{
    public enum GameState { Menu, Playing, Paused, GameOver }

    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }
        public GameState CurrentState { get; private set; }

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void StartGame() { CurrentState = GameState.Playing; SceneManager.LoadScene("Gameplay"); }
        public void PauseGame() { CurrentState = GameState.Paused; Time.timeScale = 0; }
        public void ResumeGame() { CurrentState = GameState.Playing; Time.timeScale = 1; }
        public void GameOver() { CurrentState = GameState.GameOver; }
    }
}
