---
description: "Use when designing MonoBehaviour structure — splitting responsibilities, inter-component communication patterns, component composition, execution order, and lifecycle management."
---

# Component Design

Guidance for structuring Unity MonoBehaviours: when to split responsibilities, how to wire communication between components, and how to enforce predictable lifecycle ordering.

## When to Apply

Activate when the conversation involves:
- Deciding whether to split one MonoBehaviour into multiple components
- Choosing how components should communicate (events, interfaces, direct references)
- Establishing component lifecycle order or initialization sequencing
- Reviewing a MonoBehaviour that has grown large or hard to maintain
- Naming or organizing components on a prefab
- Setting up inter-system communication on a multi-component prefab

Do NOT activate for questions about scene structure, data modeling, or prefab hierarchy — those go to `hades:unity-architect`, `hades:data-modeling`, or `hades:prefab-architecture`.

## Project Context Check

Before making recommendations:

1. **Check existing patterns in the graph:**
   - Call `find_components_using_pattern("MonoBehaviour")` to understand component density and naming conventions already in the project
   - Call `search_by_name("*Manager*")` to detect existing manager patterns and whether the project uses service-locator style aggregators
   - Call `find_prefabs_with_component("<ComponentName>")` on the focal component to find how it is already used across prefabs

2. **Check team decisions in memory:**
   - Call `recall_memory("component design patterns communication")` to find documented communication conventions
   - Check validation status — if a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt recommendations based on findings:**
   - If the project already uses event channels consistently → recommend SO event channel for new communication
   - If the project uses direct serialized references throughout → align unless there is a decoupling reason
   - After the user agrees on a pattern not yet documented, call `propose_memory_update` to record it

## Decision Framework

### Splitting Responsibilities

#### Split signals — consider extracting to a separate component when:
- The MonoBehaviour exceeds ~150 lines and the extra lines serve a clearly different concern (e.g., input logic inside a movement class)
- A behaviour is reused on multiple different prefab types
- Two concerns require different Update frequencies (e.g., physics in `FixedUpdate`, UI refresh in `LateUpdate`)
- A concern is independently testable and has no natural dependency on the other concerns in the class
- One concern needs to be toggled independently (enable/disable a component vs. controlling a flag inside a larger class)

#### Keep together — do NOT split when:
- The class is under ~100 lines and all code is part of the same sequential operation
- Splitting would produce two components that call each other constantly, replacing one class with artificial coupling
- Both concerns share mutable state so heavily that extracting one produces a data-passing tangle
- The split creates a component that is never reused anywhere else and only adds indirection

### Inter-Component Communication Decision Tree

```
How coupled should the sender and receiver be?
├── Fully decoupled — sender must not know receiver exists at all
│   └── SO Event Channel
│       - GameEvent SO asset raised from sender
│       - GameEventListener on any receiver; wired in inspector
│       - Zero code references between systems
│       - Best for: player death → UI update, enemy killed → score
│
├── One-to-many on the same or sibling GameObject
│   └── C# Events (event / Action / delegate)
│       - Sender exposes public event Action<T> OnSomething
│       - Receivers subscribe in OnEnable, unsubscribe in OnDisable
│       - No SO asset needed; garbage-free if unsubscribed properly
│       - Best for: Health.OnDied → Combat disable, Animation trigger
│
├── Polymorphic — caller needs a contract, not a concrete type
│   └── Interface + TryGetComponent<T>
│       - Define interface in its own file (IDamageable, IInteractable)
│       - Caller does: if (hit.TryGetComponent<IDamageable>(out var d)) d.TakeDamage(...)
│       - Receivers implement the interface independently
│       - Best for: weapons hitting anything damageable, trigger volumes
│
└── Tightly coupled by design — two components always coexist
    └── Direct serialized reference [SerializeField]
        - Drag the target in the inspector or GetComponent in Awake (cached)
        - Explicit, fast, easy to read
        - Best for: PlayerController → PlayerMovement on the same prefab
```

### Lifecycle Order

Unity calls these methods in this order. Use the order to decide where initialization logic belongs.

```
Awake          — Self-init only. Cache components. Do not reference other objects.
OnEnable       — Register to events/channels. Subscribe listeners.
Start          — Cross-object init. Safe to call GetComponent on other objects.
Update         — Per-frame logic (input, state machine ticks).
LateUpdate     — Anything that reads results of Update (camera follow, IK).
FixedUpdate    — Physics forces and velocity writes.
OnDisable      — Unregister from events/channels. Unsubscribe listeners.
OnDestroy      — Final cleanup (unmanaged resources, static references).
```

**Rules:**
- Cache components in `Awake`, not `Start` — other objects may call you before `Start` runs
- Always pair `OnEnable` subscriptions with `OnDisable` unsubscriptions — a disabled component must not receive events
- Never call `GetComponent` in `Update` without caching the result; call once in `Awake`

### Naming Convention

Pattern: `[ObjectType][Concern]`

| Example | Meaning |
|---------|---------|
| `PlayerMovement` | Moves the player character |
| `PlayerHealth` | Tracks and modifies player HP |
| `EnemyAI` | Drives enemy decision-making |
| `EnemyAttack` | Executes enemy attack logic |
| `ProjectileLifetime` | Despawns a projectile after duration |
| `DoorInteraction` | Handles player open/close interaction |

- Do NOT name components after their implementation (`PlayerRigidbodyUpdater`) — name after their responsibility
- Do NOT suffix with `Manager` unless the class genuinely manages a lifecycle (creating, tracking, destroying instances)

## Code Examples

### SO Event Channel (Full Implementation)

Zero-coupling communication between systems via ScriptableObject assets.

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Broadcast event with no payload. Create via Assets > Events > Game Event.
/// Raise from any sender; any number of GameEventListeners respond.
/// </summary>
[CreateAssetMenu(menuName = "Events/Game Event", fileName = "NewGameEvent")]
public class GameEvent : ScriptableObject
{
    private readonly List<GameEventListener> _listeners = new();

    public void Raise()
    {
        // Iterate backwards so listeners can safely unregister during a callback.
        for (int i = _listeners.Count - 1; i >= 0; i--)
            _listeners[i].OnEventRaised();
    }

    public void RegisterListener(GameEventListener listener)
    {
        if (!_listeners.Contains(listener))
            _listeners.Add(listener);
    }

    public void UnregisterListener(GameEventListener listener) =>
        _listeners.Remove(listener);
}

/// <summary>
/// Drop on any GameObject. Wire the SO asset to Event; add callbacks to Response.
/// Automatically registers and unregisters via OnEnable/OnDisable.
/// </summary>
public class GameEventListener : MonoBehaviour
{
    [Tooltip("The GameEvent SO this listener responds to.")]
    [SerializeField] private GameEvent _event;

    [Tooltip("Inspector-wired callbacks invoked when the event is raised.")]
    [SerializeField] private UnityEvent _response;

    private void OnEnable()  => _event.RegisterListener(this);
    private void OnDisable() => _event.UnregisterListener(this);

    public void OnEventRaised() => _response?.Invoke();
}
```

Usage: create a `PlayerDied` asset, have `PlayerHealth` call `playerDiedEvent.Raise()`, drag the asset onto any `GameEventListener` that needs to react (UI, audio, checkpoint system) — with no code references between them.

---

### Interface-Based Communication (Full Implementation)

Lets weapons, traps, and hazards damage anything without knowing the concrete type.

```csharp
using UnityEngine;

/// <summary>
/// Implemented by any object that can receive damage.
/// Weapons and hazards depend on this interface, not on concrete types.
/// </summary>
public interface IDamageable
{
    void TakeDamage(float amount, GameObject source);
    bool IsAlive { get; }
}

/// <summary>
/// Concrete implementation on a character.
/// </summary>
public class CharacterHealth : MonoBehaviour, IDamageable
{
    [SerializeField] private float _maxHealth = 100f;

    public float Current { get; private set; }
    public bool IsAlive => Current > 0f;

    private void Awake() => Current = _maxHealth;

    public void TakeDamage(float amount, GameObject source)
    {
        if (!IsAlive) return;
        Current = Mathf.Max(0f, Current - amount);
        if (!IsAlive)
            Debug.Log($"{name} was killed by {source.name}");
    }
}

/// <summary>
/// A melee weapon hitbox. Damages anything IDamageable — enemies, destructibles, etc.
/// Does not reference CharacterHealth directly.
/// </summary>
public class MeleeWeapon : MonoBehaviour
{
    [SerializeField] private float _damage = 25f;
    [SerializeField] private float _hitRadius = 0.75f;
    [SerializeField] private LayerMask _hitMask;

    public void SwingAttack()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, _hitRadius, _hitMask);
        foreach (Collider hit in hits)
        {
            if (hit.TryGetComponent<IDamageable>(out IDamageable target) && target.IsAlive)
                target.TakeDamage(_damage, gameObject);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _hitRadius);
    }
#endif
}
```

---

### Component Composition (Full Implementation)

A `PlayerController` that delegates to focused components rather than doing everything itself.

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Thin coordinator. Reads input and routes it to focused sub-components.
/// Add to the root player GameObject alongside PlayerMovement, PlayerHealth, PlayerCombat.
/// </summary>
[RequireComponent(typeof(PlayerMovement))]
[RequireComponent(typeof(PlayerHealth))]
[RequireComponent(typeof(PlayerCombat))]
public class PlayerController : MonoBehaviour
{
    private PlayerMovement _movement;
    private PlayerHealth   _health;
    private PlayerCombat   _combat;

    private void Awake()
    {
        _movement = GetComponent<PlayerMovement>();
        _health   = GetComponent<PlayerHealth>();
        _combat   = GetComponent<PlayerCombat>();
    }

    private void OnEnable()  => _health.OnDied.AddListener(HandleDeath);
    private void OnDisable() => _health.OnDied.RemoveListener(HandleDeath);

    // Called by Unity's Input System via Player Input component
    public void OnMove(InputValue value)   => _movement.SetInputDirection(value.Get<Vector2>());
    public void OnAttack(InputValue value) => _combat.TryAttack();

    private void HandleDeath()
    {
        _movement.enabled = false;
        _combat.enabled   = false;
        // Trigger death animation, notify game state, etc.
    }
}

// ── PlayerMovement ────────────────────────────────────────────────────────────

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [SerializeField] private float _speed = 5f;
    [SerializeField] private float _gravity = -9.81f;

    private CharacterController _cc;
    private Vector2 _inputDir;
    private float   _verticalVelocity;

    private void Awake() => _cc = GetComponent<CharacterController>();

    public void SetInputDirection(Vector2 dir) => _inputDir = dir;

    private void Update()
    {
        Vector3 move = new Vector3(_inputDir.x, 0f, _inputDir.y) * _speed;

        if (_cc.isGrounded)
            _verticalVelocity = -0.5f;
        else
            _verticalVelocity += _gravity * Time.deltaTime;

        move.y = _verticalVelocity;
        _cc.Move(move * Time.deltaTime);
    }
}

// ── PlayerHealth ──────────────────────────────────────────────────────────────

public class PlayerHealth : MonoBehaviour, IDamageable
{
    [SerializeField] private float _maxHealth = 100f;

    public float Current { get; private set; }
    public bool IsAlive => Current > 0f;

    public UnityEngine.Events.UnityEvent<float> OnHealthChanged;
    public UnityEngine.Events.UnityEvent OnDied;

    private void Awake() => Current = _maxHealth;

    public void TakeDamage(float amount, GameObject source)
    {
        if (!IsAlive) return;
        Current = Mathf.Max(0f, Current - amount);
        OnHealthChanged?.Invoke(Current);
        if (!IsAlive) OnDied?.Invoke();
    }

    public void Heal(float amount)
    {
        if (!IsAlive) return;
        Current = Mathf.Min(_maxHealth, Current + amount);
        OnHealthChanged?.Invoke(Current);
    }
}

// ── PlayerCombat ──────────────────────────────────────────────────────────────

public class PlayerCombat : MonoBehaviour
{
    [SerializeField] private float _damage     = 20f;
    [SerializeField] private float _range      = 1.2f;
    [SerializeField] private float _cooldown   = 0.5f;
    [SerializeField] private LayerMask _enemyMask;

    private float _nextAttackTime;

    public bool TryAttack()
    {
        if (Time.time < _nextAttackTime) return false;

        Collider[] hits = Physics.OverlapSphere(transform.position, _range, _enemyMask);
        foreach (Collider hit in hits)
        {
            if (hit.TryGetComponent<IDamageable>(out IDamageable target))
                target.TakeDamage(_damage, gameObject);
        }

        _nextAttackTime = Time.time + _cooldown;
        return true;
    }
}
```

---

### RequireComponent Usage Pattern

Declares dependencies at the class level so Unity enforces them at authoring time.

```csharp
using UnityEngine;

/// <summary>
/// Enforces that Rigidbody and Collider exist before this component can be added.
/// Unity prevents removing the required components while this script is attached.
/// Use for hard runtime dependencies — not optional dependencies.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class PhysicsCharacter : MonoBehaviour
{
    // Cache in Awake — guaranteed to exist because of RequireComponent.
    private Rigidbody _rb;
    private CapsuleCollider _col;

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<CapsuleCollider>();
    }
}
```

Only use `[RequireComponent]` for dependencies that are always needed. For optional dependencies, check with `TryGetComponent` in `Start`.

## Anti-Examples

### God Component

```csharp
// BAD — one class handles input, physics movement, health, UI, audio, and saving.
// Every new feature requires reading and understanding all other features.
// Cannot test any single concern in isolation.
public class PlayerController : MonoBehaviour
{
    public float health = 100f;
    public float speed = 5f;
    public Text healthText;
    public AudioSource footstepAudio;

    void Update()
    {
        float h = Input.GetAxis("Horizontal");
        transform.Translate(h * speed * Time.deltaTime, 0, 0);
        healthText.text = $"HP: {health}";
        if (Mathf.Abs(h) > 0.1f) footstepAudio.Play();
        PlayerPrefs.SetFloat("health", health); // saves every frame
        // ... 900 more lines
    }
}
```

Prefer: `PlayerMovement`, `PlayerHealth`, `PlayerHud`, `PlayerAudio` components wired via C# events or SO event channels.

---

### SendMessage / BroadcastMessage

```csharp
// BAD — string-based dispatch with no compile-time safety, no IDE navigation,
// allocates strings, and silently does nothing if the method name is wrong.
void OnTriggerEnter(Collider other)
{
    other.SendMessage("TakeDamage", 10f, SendMessageOptions.DontRequireReceiver);
}

// GOOD — use an interface instead
void OnTriggerEnter(Collider other)
{
    if (other.TryGetComponent<IDamageable>(out IDamageable target))
        target.TakeDamage(10f, gameObject);
}
```

---

### GetComponent in Update Without Caching

```csharp
// BAD — GetComponent allocates a component lookup every frame.
// At 60 fps with 100 enemies this is 6,000 lookups/second.
void Update()
{
    GetComponent<Rigidbody>().AddForce(Vector3.up);
}

// GOOD — cache once in Awake
private Rigidbody _rb;
void Awake() => _rb = GetComponent<Rigidbody>();
void Update() => _rb.AddForce(Vector3.up);
```

---

### Public Fields Instead of [SerializeField]

```csharp
// BAD — public field exposes the value to all other code.
// Any script can write to it, making state tracking hard.
public float speed = 5f;

// GOOD — [SerializeField] private keeps the field internal;
// expose a read-only property if other code needs to read it.
[SerializeField] private float _speed = 5f;
public float Speed => _speed;
```

## Cross-References

- Related skills: `hades:unity-architect`, `hades:data-modeling`, `hades:prefab-architecture`
- Review skill: `hades:unity-reviewer`
- Hades MCP tools: `find_components_using_pattern`, `find_prefabs_with_component`, `search_by_name`, `recall_memory`, `propose_memory_update`
- Unity docs: [MonoBehaviour lifecycle](https://docs.unity3d.com/6000.0/Documentation/Manual/ExecutionOrder.html), [RequireComponent](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RequireComponent.html), [ScriptableObject Events](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ScriptableObject.html)
