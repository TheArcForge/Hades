using UnityEngine;

namespace TestProject.Systems
{
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }
        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioSource sfxSource;

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void PlaySFX(AudioClip clip) { sfxSource.PlayOneShot(clip); }
        public void PlayMusic(AudioClip clip) { musicSource.clip = clip; musicSource.Play(); }
    }
}
