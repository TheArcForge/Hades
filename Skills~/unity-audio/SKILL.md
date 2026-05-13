---
description: "Use when implementing audio systems — audio manager architecture, AudioMixer setup, spatial audio, event-driven audio, music systems, and sound effect management patterns."
---

# Unity Audio Systems

Guidance for architecting audio in Unity 6: centralized vs distributed approaches, AudioMixer group hierarchies, ScriptableObject-driven event audio, spatial audio configuration, music crossfading, and sound pooling for high-frequency effects.

## When to Apply

Activate when the conversation involves:
- Adding a first audio system to a project (what architecture to start with)
- Deciding how SFX should be triggered from gameplay code (direct call vs event channel)
- Setting up AudioMixer groups, snapshots, or send/receive effects
- Implementing music that transitions between tracks or layers adaptively
- Configuring 3D spatial audio: rolloff curves, reverb zones, occlusion
- Pooling audio sources for rapid-fire sounds (gunshots, footsteps)
- Mixing and volume control including accessibility (separate sliders for Music/SFX/UI)

Do NOT activate for visual effects triggered alongside audio, animation state that happens to play a sound, or UI layout for volume sliders — those go to `hades:unity-vfx`, `hades:component-design`, or `hades:unity-ui`.

## Project Context Check

Before making recommendations:

1. **Check existing audio usage in the graph:**
   - Call `find_components_using_pattern("AudioSource")` — count how many objects carry AudioSource directly; if many, the project likely uses a distributed model
   - Call `search_by_name("*Mixer*")` — detect whether an AudioMixer asset already exists; if so, match its group structure
   - Call `search_by_name("*Audio*")` — find existing audio managers, event assets, and listener scripts

2. **Check team decisions in memory:**
   - Call `recall_memory("audio architecture mixer")` to surface documented audio decisions or constraints
   - Check validation status — if a recalled decision shows `warning`, surface the conflict before proceeding

3. **Adapt recommendations based on findings:**
   - If no AudioMixer exists → recommend creating one as a first step before wiring any volume controls
   - If the project already uses SO event channels elsewhere → favour SO audio events for consistency
   - If distributed AudioSources are pervasive and team is happy with them → recommend adding a mixer and pool rather than a full rewrite
   - After the user agrees on an approach not yet documented, call `propose_memory_update` to record it

## Decision Framework

### Centralized vs Distributed Audio

```
How many objects need to play sounds?
├── A handful of distinct, authored objects (e.g. doors, NPCs)
│   └── Distributed: AudioSource on each prefab
│       - Straightforward; position follows the GameObject automatically
│       - Works well when clips are tightly coupled to a specific object
│       - Add a mixer output route so volumes are still controlled centrally
│
└── Many objects, pooled objects, or gameplay events from non-audio systems
    └── Centralized AudioManager + pool
        - A singleton service owns all AudioSources
        - Callers pass a SoundDefinition SO; manager picks an available source
        - Enables pooling, priority, and per-category volume without coupling
        - Required for: UI sounds, footstep systems, impact sounds, music
```

**Rule of thumb:** any sound that must play on objects that are frequently instantiated/destroyed (projectiles, particles, enemies) must go through a pool. Direct `AudioSource.PlayOneShot` on those objects leaks sources when the object is destroyed mid-clip.

### AudioMixer Architecture

Structure the mixer as a fixed hierarchy of bus groups:

```
Master
├── Music         — background music; controls occlusion ducking send
├── SFX
│   ├── World     — 3D in-world effects (impacts, ambient loops)
│   ├── Character — player/enemy vocalizations, footsteps
│   └── UI        — button clicks, menu transitions (bypass spatialization)
└── Ambience      — environmental loops (wind, crowd, rain)
```

- Each leaf group maps to one `AudioMixerGroup` reference in code / SOs.
- Expose volume parameters named `MasterVol`, `MusicVol`, `SFXVol`, `UIVol` — link these to the Settings screen.
- Use **Snapshots** for state-driven mix changes (gameplay vs paused vs cutscene) rather than animating individual parameters from code.
- Use **Send/Receive** effects on the Music group with a Duck Volume on SFX.World to automatically duck world sounds during dialogue or cut-scenes.

### Event-Driven Audio Patterns

```
Who knows what sound to play?
├── The sound is authored on the asset / prefab (footstep, sword swing)
│   └── AudioSource on prefab, or SoundDefinition SO dragged into prefab field
│       - Serialize [SerializeField] SoundDefinition _footstepSound
│       - Call AudioManager.Instance.Play(_footstepSound, transform.position)
│
└── Gameplay event triggers audio from a different system (player died → UI stinger)
    └── SO Event Channel + AudioEventListener
        - GameEvent SO raised by gameplay; AudioEventListener responds
        - AudioEventListener serializes a SoundDefinition; no code coupling
        - Identical pattern to the component-design SO event channel
```

### Spatial Audio Configuration (3D)

Key fields per AudioSource in a 3D context:

| Field | Recommendation |
|-------|---------------|
| `Spatial Blend` | 1.0 for world sounds, 0.0 for UI/music |
| `Volume Rolloff` | Logarithmic for realism; Custom for game-feel tuning |
| `Min Distance` | Radius at full volume — tune per sound category |
| `Max Distance` | Hard cutoff — keep low for performance (e.g. 30 m for footsteps) |
| `Doppler Level` | 0 unless vehicle/projectile whoosh is explicitly needed |
| `Reverb Zone Mix` | 1.0 to participate in scene reverb zones |

Add `AudioReverbZone` components in scene areas that need acoustic character (cave, cathedral) rather than applying reverb globally via the mixer.

### Music System

- Never use `PlayOneShot` for music — it bypasses looping and cannot be stopped.
- Crossfade by lerping two AudioSources' volumes over time; don't cut between them.
- For adaptive/layered music, keep stems on separate AudioSources sharing the same `timeSamples` so they stay in sync.

## Code Examples

### Audio Manager Singleton with SO Sound Definitions

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Define a sound in the Project. Assign a clip, mixer group, volume range, etc.
/// Create via Assets > Audio > Sound Definition.
/// </summary>
[CreateAssetMenu(menuName = "Audio/Sound Definition", fileName = "SFX_New")]
public class SoundDefinition : ScriptableObject
{
    [Tooltip("One or more clips; a random one is chosen each play.")]
    public AudioClip[] Clips;

    public AudioMixerGroup MixerGroup;

    [Range(0f, 1f)] public float VolumeMin = 0.9f;
    [Range(0f, 1f)] public float VolumeMax = 1.0f;

    [Range(0.8f, 1.2f)] public float PitchMin = 0.95f;
    [Range(0.8f, 1.2f)] public float PitchMax = 1.05f;

    [Tooltip("0 = 2D, 1 = full 3D positional.")]
    [Range(0f, 1f)] public float SpatialBlend = 1f;

    public float MinDistance = 1f;
    public float MaxDistance = 20f;

    public bool Loop = false;

    /// <summary>Returns a random clip from the array, or null if none assigned.</summary>
    public AudioClip GetClip()
    {
        if (Clips == null || Clips.Length == 0) return null;
        return Clips[Random.Range(0, Clips.Length)];
    }
}

/// <summary>
/// Central audio service. Access via AudioManager.Instance.
/// Manages a pool of AudioSources; callers supply a SoundDefinition SO.
/// Add to a persistent GameObject (DontDestroyOnLoad) in your bootstrap scene.
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Tooltip("Number of pooled AudioSources. Increase if sounds are cut off.")]
    [SerializeField] private int _poolSize = 16;

    private readonly Queue<AudioSource> _pool = new();
    private readonly List<AudioSource>  _active = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        BuildPool();
    }

    private void Update()
    {
        // Return finished sources to the pool.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            AudioSource src = _active[i];
            if (!src.isPlaying)
            {
                _active.RemoveAt(i);
                ReturnToPool(src);
            }
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Play a one-shot sound at a world position.</summary>
    public AudioSource Play(SoundDefinition def, Vector3 position = default)
    {
        if (def == null) return null;
        AudioClip clip = def.GetClip();
        if (clip == null) return null;

        AudioSource src = RentFromPool();
        Apply(src, def, clip, position);
        src.Play();
        _active.Add(src);
        return src;
    }

    /// <summary>Play and attach to a moving transform (e.g. character footstep).</summary>
    public AudioSource PlayAttached(SoundDefinition def, Transform parent)
    {
        AudioSource src = Play(def, parent.position);
        if (src != null) src.transform.SetParent(parent, worldPositionStays: true);
        return src;
    }

    /// <summary>Stop a specific source and immediately return it to the pool.</summary>
    public void Stop(AudioSource src)
    {
        if (src == null) return;
        src.Stop();
        _active.Remove(src);
        ReturnToPool(src);
    }

    // ── Pool helpers ───────────────────────────────────────────────────────────

    private void BuildPool()
    {
        for (int i = 0; i < _poolSize; i++)
        {
            var go  = new GameObject($"AudioSource_{i:00}");
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            go.SetActive(false);
            _pool.Enqueue(src);
        }
    }

    private AudioSource RentFromPool()
    {
        if (_pool.Count == 0)
        {
            // Pool exhausted: steal the oldest active source (lowest priority).
            AudioSource steal = _active[0];
            steal.Stop();
            _active.RemoveAt(0);
            steal.transform.SetParent(transform);
            return steal;
        }

        AudioSource src = _pool.Dequeue();
        src.gameObject.SetActive(true);
        return src;
    }

    private void ReturnToPool(AudioSource src)
    {
        src.Stop();
        src.clip = null;
        src.transform.SetParent(transform);
        src.transform.localPosition = Vector3.zero;
        src.gameObject.SetActive(false);
        _pool.Enqueue(src);
    }

    private static void Apply(AudioSource src, SoundDefinition def, AudioClip clip, Vector3 pos)
    {
        src.transform.position = pos;
        src.clip              = clip;
        src.outputAudioMixerGroup = def.MixerGroup;
        src.volume            = Random.Range(def.VolumeMin, def.VolumeMax);
        src.pitch             = Random.Range(def.PitchMin,  def.PitchMax);
        src.spatialBlend      = def.SpatialBlend;
        src.minDistance       = def.MinDistance;
        src.maxDistance       = def.MaxDistance;
        src.loop              = def.Loop;
        src.rolloffMode       = AudioRolloffMode.Logarithmic;
    }
}
```

---

### AudioMixer Group Setup (Volume Control via Exposed Parameters)

```csharp
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Reads volume settings (0–1 sliders) and writes them to AudioMixer exposed parameters.
/// Converts linear 0–1 to decibels because AudioMixer uses dB internally.
/// Attach to a persistent settings manager or bind directly from the Settings UI.
/// </summary>
public class AudioMixerController : MonoBehaviour
{
    [SerializeField] private AudioMixer _masterMixer;

    // Exposed parameter names must match exactly what is exposed in the AudioMixer asset.
    private const string MasterParam  = "MasterVol";
    private const string MusicParam   = "MusicVol";
    private const string SFXParam     = "SFXVol";
    private const string UIParam      = "UIVol";

    // ── Volume setters (call from UI sliders) ──────────────────────────────────

    public void SetMasterVolume(float linear) => SetVolume(MasterParam, linear);
    public void SetMusicVolume(float linear)  => SetVolume(MusicParam,  linear);
    public void SetSFXVolume(float linear)    => SetVolume(SFXParam,    linear);
    public void SetUIVolume(float linear)     => SetVolume(UIParam,     linear);

    // ── Snapshot transitions ───────────────────────────────────────────────────

    /// <summary>Transition to a named snapshot over transitionTime seconds.</summary>
    public void TransitionToSnapshot(string snapshotName, float transitionTime = 0.5f)
    {
        AudioMixerSnapshot snap = _masterMixer.FindSnapshot(snapshotName);
        if (snap != null)
            snap.TransitionTo(transitionTime);
        else
            Debug.LogWarning($"AudioMixerController: snapshot '{snapshotName}' not found.");
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    public void SaveVolumes()
    {
        _masterMixer.GetFloat(MasterParam, out float master);
        _masterMixer.GetFloat(MusicParam,  out float music);
        _masterMixer.GetFloat(SFXParam,    out float sfx);
        _masterMixer.GetFloat(UIParam,     out float ui);

        PlayerPrefs.SetFloat(MasterParam, DecibelToLinear(master));
        PlayerPrefs.SetFloat(MusicParam,  DecibelToLinear(music));
        PlayerPrefs.SetFloat(SFXParam,    DecibelToLinear(sfx));
        PlayerPrefs.SetFloat(UIParam,     DecibelToLinear(ui));
        PlayerPrefs.Save();
    }

    public void LoadVolumes()
    {
        SetVolume(MasterParam, PlayerPrefs.GetFloat(MasterParam, 1f));
        SetVolume(MusicParam,  PlayerPrefs.GetFloat(MusicParam,  1f));
        SetVolume(SFXParam,    PlayerPrefs.GetFloat(SFXParam,    1f));
        SetVolume(UIParam,     PlayerPrefs.GetFloat(UIParam,     1f));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetVolume(string param, float linear)
    {
        // AudioMixer expects dB; clamp linear to avoid log(0).
        float db = linear > 0.0001f ? Mathf.Log10(linear) * 20f : -80f;
        _masterMixer.SetFloat(param, db);
    }

    private static float DecibelToLinear(float db) => Mathf.Pow(10f, db / 20f);
}
```

---

### SO Event-Driven Audio: SoundEvent + AudioEventListener

```csharp
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// ScriptableObject that pairs a gameplay GameEvent with a SoundDefinition.
/// Create via Assets > Audio > Sound Event.
/// Raise the inner GameEvent from gameplay code; AudioEventListener plays the sound.
/// No code coupling between the gameplay system and the audio system.
/// </summary>
[CreateAssetMenu(menuName = "Audio/Sound Event", fileName = "SoundEvent_New")]
public class SoundEvent : ScriptableObject
{
    [Tooltip("Trigger this event from gameplay code. Listeners respond with audio.")]
    public GameEvent Trigger;

    [Tooltip("Sound to play when the event fires.")]
    public SoundDefinition Sound;
}

/// <summary>
/// Drop on any GameObject. Wire a SoundEvent SO; the component listens to its
/// GameEvent and plays the associated SoundDefinition at this transform's position.
/// </summary>
public class AudioEventListener : MonoBehaviour
{
    [SerializeField] private SoundEvent _soundEvent;

    private GameEventListener _gameEventListener;

    private void Awake()
    {
        // Programmatically create and configure a GameEventListener so this
        // component only needs one inspector reference.
        _gameEventListener = gameObject.AddComponent<GameEventListener>();
    }

    private void OnEnable()
    {
        if (_soundEvent?.Trigger == null) return;
        _soundEvent.Trigger.RegisterListener(_gameEventListener);

        var response = new UnityEvent();
        response.AddListener(PlaySound);
        // GameEventListener.OnEventRaised is invoked by GameEvent.Raise()
        // We wire our local callback here.
        _gameEventListener.OnEventRaised = PlaySound;
    }

    private void OnDisable()
    {
        _soundEvent?.Trigger?.UnregisterListener(_gameEventListener);
    }

    private void PlaySound()
    {
        if (_soundEvent?.Sound == null) return;
        AudioManager.Instance.Play(_soundEvent.Sound, transform.position);
    }
}

// ── Minimal GameEventListener extension to support callback wiring ────────────
// (If your GameEventListener already exposes a delegate, skip this section.)

public partial class GameEventListener
{
    /// <summary>Programmatic callback; takes priority over inspector UnityEvent.</summary>
    public System.Action OnEventRaised;

    // Patch OnEventRaised to invoke the delegate when set:
    // Original method calls _response.Invoke(); add the delegate call here.
}
```

---

### Music Manager with Crossfade

```csharp
using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Manages music playback via two AudioSources that alternate during crossfades.
/// Supports looping tracks, adaptive layer muting, and snapshot-based mix changes.
/// Place on the same persistent GameObject as AudioManager.
/// </summary>
public class MusicManager : MonoBehaviour
{
    public static MusicManager Instance { get; private set; }

    [Header("Sources (configured in Inspector or auto-created)")]
    [SerializeField] private AudioSource _sourceA;
    [SerializeField] private AudioSource _sourceB;

    [Header("Mixer")]
    [SerializeField] private AudioMixerGroup _musicGroup;

    [Header("Defaults")]
    [SerializeField] private float _defaultCrossfadeDuration = 1.5f;

    private AudioSource _current;
    private AudioSource _next;
    private Coroutine   _crossfadeRoutine;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureSources();
        _current = _sourceA;
        _next    = _sourceB;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Start playing a clip immediately (no crossfade).</summary>
    public void Play(AudioClip clip, float volume = 1f)
    {
        _current.clip   = clip;
        _current.volume = volume;
        _current.loop   = true;
        _current.Play();
    }

    /// <summary>Crossfade from current track to a new clip.</summary>
    public void CrossfadeTo(AudioClip clip, float duration = -1f, float targetVolume = 1f)
    {
        if (duration < 0f) duration = _defaultCrossfadeDuration;

        // Prepare the inactive source.
        _next.clip          = clip;
        _next.volume        = 0f;
        _next.loop          = true;
        _next.timeSamples   = 0;
        _next.Play();

        if (_crossfadeRoutine != null) StopCoroutine(_crossfadeRoutine);
        _crossfadeRoutine = StartCoroutine(CrossfadeRoutine(duration, targetVolume));
    }

    /// <summary>Stop music with a fade-out.</summary>
    public void Stop(float fadeTime = 1f)
    {
        StartCoroutine(FadeOutRoutine(_current, fadeTime));
    }

    // ── Adaptive layers ────────────────────────────────────────────────────────

    /// <summary>
    /// For layered adaptive music: keep multiple AudioSources in sync by
    /// copying timeSamples from the primary source before playing a layer.
    /// </summary>
    public void SyncLayerTo(AudioSource layer, AudioClip layerClip)
    {
        layer.clip        = layerClip;
        layer.timeSamples = _current.timeSamples;
        layer.loop        = true;
        layer.Play();
    }

    // ── Coroutines ─────────────────────────────────────────────────────────────

    private IEnumerator CrossfadeRoutine(float duration, float targetVolume)
    {
        float elapsed      = 0f;
        float startVolume  = _current.volume;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t  = Mathf.Clamp01(elapsed / duration);
            _current.volume = Mathf.Lerp(startVolume,  0f,           t);
            _next.volume    = Mathf.Lerp(0f,           targetVolume, t);
            yield return null;
        }

        _current.Stop();
        _current.clip = null;

        // Swap references so _current always points to the playing source.
        (_current, _next) = (_next, _current);
        _crossfadeRoutine = null;
    }

    private IEnumerator FadeOutRoutine(AudioSource src, float duration)
    {
        float start   = src.volume;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed    += Time.unscaledDeltaTime;
            src.volume  = Mathf.Lerp(start, 0f, elapsed / duration);
            yield return null;
        }
        src.Stop();
        src.clip = null;
    }

    // ── Setup ──────────────────────────────────────────────────────────────────

    private void EnsureSources()
    {
        if (_sourceA == null)
        {
            _sourceA = gameObject.AddComponent<AudioSource>();
            _sourceA.playOnAwake = false;
            _sourceA.outputAudioMixerGroup = _musicGroup;
        }
        if (_sourceB == null)
        {
            _sourceB = gameObject.AddComponent<AudioSource>();
            _sourceB.playOnAwake = false;
            _sourceB.outputAudioMixerGroup = _musicGroup;
        }
    }
}
```

---

### Sound Pool for Rapid-Fire Effects

The pool is built into `AudioManager` above. For callers that need rapid-fire one-shots from a single source (e.g. a machine gun firing 10 rounds/second), use `AudioSource.PlayOneShot` on a dedicated per-object source rather than renting from the shared pool each time:

```csharp
using UnityEngine;

/// <summary>
/// For high-frequency sounds on a single object (rapid gunfire, fast footsteps).
/// Uses one dedicated AudioSource with PlayOneShot to allow overlapping clips
/// without needing multiple pool sources.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class RapidFireAudio : MonoBehaviour
{
    [SerializeField] private SoundDefinition _fireSoundDef;

    private AudioSource _audioSource;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        // Route through the mixer group from the SO.
        if (_fireSoundDef?.MixerGroup != null)
            _audioSource.outputAudioMixerGroup = _fireSoundDef.MixerGroup;
        _audioSource.spatialBlend = _fireSoundDef?.SpatialBlend ?? 1f;
        _audioSource.playOnAwake  = false;
    }

    /// <summary>Call from the weapon's fire logic each time a shot fires.</summary>
    public void PlayFireSound()
    {
        if (_fireSoundDef == null) return;
        AudioClip clip = _fireSoundDef.GetClip();
        if (clip == null) return;

        // PlayOneShot allows many overlapping clips on one AudioSource.
        float volume = Random.Range(_fireSoundDef.VolumeMin, _fireSoundDef.VolumeMax);
        _audioSource.pitch = Random.Range(_fireSoundDef.PitchMin, _fireSoundDef.PitchMax);
        _audioSource.PlayOneShot(clip, volume);
    }
}
```

Use `PlayOneShot` ONLY for rapid-fire effects on a stable (non-destroyed) source. Do NOT use it for music or for sounds on objects that can be destroyed mid-clip.

## Anti-Examples

### AudioSource on Every Object

```csharp
// BAD — each enemy, projectile, and pickup carries its own AudioSource.
// 200 enemies = 200 sources. Destroyed objects cut off their clips mid-play.
// No mixing, no pooling, no volume control.
public class Enemy : MonoBehaviour
{
    public AudioSource audioSource; // public field, no mixer group
    public AudioClip deathClip;

    void Die()
    {
        audioSource.PlayOneShot(deathClip); // cut off when Destroy() is called
        Destroy(gameObject);
    }
}

// GOOD — request a pooled source from the manager instead.
public class Enemy : MonoBehaviour
{
    [SerializeField] private SoundDefinition _deathSound;

    void Die()
    {
        AudioManager.Instance.Play(_deathSound, transform.position);
        Destroy(gameObject); // pool source continues playing independently
    }
}
```

---

### Loading All Clips at Startup

```csharp
// BAD — loads every audio clip into memory at boot.
// Large games can consume hundreds of MB of RAM from audio alone.
void Awake()
{
    AudioClip[] allClips = Resources.LoadAll<AudioClip>("Audio");
    // store all clips...
}

// GOOD — use Addressables for large clip libraries; load per-scene or on demand.
// SoundDefinition SOs keep clip references; Addressables unloads them when not needed.
```

---

### PlayOneShot for Music

```csharp
// BAD — PlayOneShot cannot loop, cannot be stopped gracefully,
// cannot be crossfaded, and bypasses volume control via the mixer.
void StartMusic()
{
    audioSource.PlayOneShot(backgroundMusicClip);
}

// GOOD — use MusicManager.Play() or MusicManager.CrossfadeTo() instead.
```

---

### Hardcoded Volume Values

```csharp
// BAD — buries volume in code; impossible to adjust from Settings UI.
// No accessibility support for players with hearing differences.
audioSource.volume = 0.7f;

// GOOD — all volumes live in SoundDefinition SOs and AudioMixer exposed parameters.
// UI sliders call AudioMixerController.SetMusicVolume(sliderValue).
```

## Cross-References

- Related skills: `hades:component-design`, `hades:data-modeling`, `hades:unity-performance`
- Hades MCP tools: `find_components_using_pattern`, `search_by_name`, `recall_memory`, `propose_memory_update`
- Unity docs: [AudioMixer](https://docs.unity3d.com/6000.0/Documentation/Manual/AudioMixer.html), [AudioSource](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AudioSource.html), [Audio Spatializer SDK](https://docs.unity3d.com/6000.0/Documentation/Manual/AudioSpatializerSDK.html)
