---
description: "Use when implementing input handling — new Input System setup, action maps, multi-device support, input rebinding, local multiplayer input, and input abstraction patterns."
---

# Unity Input Systems

Guidance for implementing input in Unity 6: new Input System vs legacy, action map organization, callback vs polling patterns, input abstraction for testability, runtime rebinding with persistence, and local multiplayer with PlayerInputManager.

## When to Apply

Activate when the conversation involves:
- Choosing between the new Input System package and the legacy Input Manager
- Structuring Input Action Assets and their action maps
- Deciding whether to use polling (checking each frame) vs callbacks (events on change)
- Abstracting input so components do not depend on device-specific APIs
- Implementing runtime key/button rebinding with UI and persistence
- Setting up local multiplayer with split-screen or separate control schemes
- Supporting multiple simultaneous devices (gamepad + keyboard/mouse)

Do NOT activate for UI layout of a rebinding screen, game feel tuning (deadzone curves, acceleration), or platform-specific store requirements — those go to `hades:unity-ui`, `hades:unity-performance`, or a platform-specific reference.

## Project Context Check

Before making recommendations:

1. **Check existing input setup in the graph:**
   - Call `search_by_name("*.inputactions")` — if an Input Action Asset exists, examine its action maps before recommending new ones; match the existing naming style
   - Call `find_components_using_pattern("PlayerInput")` — detects whether the project already uses the new Input System's `PlayerInput` component, and on which prefabs
   - Call `find_components_using_pattern("Input")` — surfaces legacy `Input.GetKey`/`Input.GetAxis` usage; note how widespread it is before recommending a migration

2. **Check team decisions in memory:**
   - Call `recall_memory("input system controls rebinding")` to surface documented input decisions or control schemes
   - Check validation status — if a recalled decision shows `warning`, surface the conflict before proceeding

3. **Adapt recommendations based on findings:**
   - If `.inputactions` asset exists → extend it rather than creating a new one
   - If legacy input is isolated to one or two scripts → recommend wrapping behind an interface before migrating
   - If `PlayerInput` is already in use → align new work with the existing callback mode (Unity Events, C# Events, or Invoke Unity Events)
   - After the user agrees on an approach not yet documented, call `propose_memory_update` to record it

## Decision Framework

### New Input System vs Legacy Input Manager

```
Starting a new project?
└── Always use the new Input System package
    - Multi-device support out of the box (keyboard+mouse, gamepad, touch, XR)
    - Action maps provide context-aware input (Player vs UI vs Vehicle)
    - Runtime rebinding is built in
    - Testable via InputTestFixture

Existing project with legacy Input.GetKey / Input.GetAxis?
├── Isolated usage (1–3 scripts) → wrap behind IInputProvider interface first,
│   then swap implementation to new Input System without touching callers
└── Widespread usage (10+ call sites) → migrate incrementally:
    - Enable both systems via Project Settings > Player > Active Input Handling = Both
    - Migrate one system at a time; test after each
    - Remove legacy calls last
```

**Never mix `Input.GetKey` calls and Input System callbacks in the same logical feature.** Pick one for each feature and keep it consistent.

### Action Map Organization

```
Recommended action map structure:
├── Player          — gameplay movement, look, attack, interact
│                      Enable during gameplay; disable on menus
├── UI              — navigate, submit, cancel, point, click
│                      Driven by EventSystem; rarely need custom code
├── Menu            — pause, back, open inventory
│                      Can coexist with Player map if needed
└── Vehicle         — throttle, brake, steer (if applicable)
                       Separate map so Player actions do not bleed in

Rules:
- One action per semantic intention: "Jump" not "SpaceBar"
- Never name actions after physical keys — binding maps keys to actions, not vice versa
- Group by context, not by device: "Move" works for both stick and WASD
```

### Input Patterns: Polling vs Callbacks

```
Should I read input in Update or subscribe to events?
├── Value that changes continuously (movement axis, aim vector)
│   └── Polling — read InputAction.ReadValue<Vector2>() in Update
│       - Simple and predictable
│       - No missed frames for smooth analog input
│
├── Discrete event that fires once (Jump pressed, Attack started)
│   └── Callbacks — subscribe to action.performed / action.canceled
│       - Fired exactly once per press/release
│       - Subscribe in OnEnable, unsubscribe in OnDisable
│       - Never miss an event even at low frame rates
│
└── Composite (Hold, Double-tap, Sequence)
    └── Use InputSystem interaction types (HoldInteraction, TapInteraction)
        - Declare in the .inputactions asset, not in code
        - action.performed fires only when the interaction completes
```

### Multi-Device Support

- Create one **Control Scheme** per device category in the `.inputactions` asset: `Keyboard&Mouse`, `Gamepad`.
- Bind each action to both schemes; the Input System auto-routes based on last-used device.
- For UI that shows input hints: subscribe to `InputSystem.onActionChange` or `PlayerInput.onControlsChanged` to swap icon sets when the active device changes.
- For mobile: add a `Touch` scheme and a virtual joystick component that writes to a `Vector2` action binding via `InputSystem.AddControl`.

### Local Multiplayer

- Use `PlayerInputManager` component to spawn player prefabs and assign control schemes automatically.
- Each spawned player gets its own `PlayerInput` component, isolated from others.
- For split-screen: enable `PlayerInputManager.splitScreen` and configure `fixedNumberOfSplitScreens`.
- Pass a `PlayerInput` reference into the player's controller at spawn to avoid singletons:

```
PlayerInputManager.PlayerJoinedEvent → PlayerController.Initialize(PlayerInput input)
```

## Code Examples

### Input Action Asset Setup (C# Generated Class)

Enable "Generate C# Class" on the `.inputactions` asset inspector. Unity generates a strongly-typed wrapper — no magic strings.

```csharp
// Auto-generated from PlayerInputActions.inputactions (Unity 6)
// Example: InputActions asset with Player and UI action maps.
// Generated class name matches the asset name.

// Usage pattern — do not copy-paste the generated class itself; let Unity generate it.
// This shows the CALLING pattern.

using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Component that owns the input actions asset and distributes actions to handlers.
/// Place on the player root prefab alongside PlayerMovement, PlayerCombat, etc.
/// </summary>
public class PlayerInputHandler : MonoBehaviour
{
    // Populated via Generate C# Class on the .inputactions asset.
    private PlayerInputActions _actions;

    // External systems receive processed values — not raw InputActions.
    [SerializeField] private PlayerMovement  _movement;
    [SerializeField] private PlayerCombat    _combat;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        _actions = new PlayerInputActions();
    }

    private void OnEnable()
    {
        _actions.Player.Enable();

        // Discrete actions → callbacks.
        _actions.Player.Jump.performed   += OnJump;
        _actions.Player.Attack.performed += OnAttack;
        _actions.Player.Dodge.performed  += OnDodge;
    }

    private void OnDisable()
    {
        _actions.Player.Jump.performed   -= OnJump;
        _actions.Player.Attack.performed -= OnAttack;
        _actions.Player.Dodge.performed  -= OnDodge;

        _actions.Player.Disable();
    }

    private void OnDestroy() => _actions.Dispose();

    // ── Update: continuous values ──────────────────────────────────────────────

    private void Update()
    {
        // Analog / continuous values are polled each frame.
        Vector2 move = _actions.Player.Move.ReadValue<Vector2>();
        Vector2 look = _actions.Player.Look.ReadValue<Vector2>();

        _movement.SetMoveInput(move);
        _movement.SetLookInput(look);
    }

    // ── Callbacks: discrete events ─────────────────────────────────────────────

    private void OnJump(InputAction.CallbackContext ctx)   => _movement.RequestJump();
    private void OnAttack(InputAction.CallbackContext ctx) => _combat.TryAttack();
    private void OnDodge(InputAction.CallbackContext ctx)  => _movement.RequestDodge();
}
```

---

### Input Abstraction Layer (IInputProvider)

Decouples `PlayerMovement` and other consumers from any specific input backend. Enables unit-testing movement logic without a real device.

```csharp
using UnityEngine;

// ── Interface ──────────────────────────────────────────────────────────────────

/// <summary>
/// Contract for providing player input values.
/// Gameplay components (PlayerMovement, PlayerCombat) depend ONLY on this interface.
/// Swap implementations for real devices, AI control, or test fixtures.
/// </summary>
public interface IInputProvider
{
    Vector2 MoveInput  { get; }
    Vector2 LookInput  { get; }
    bool    JumpPressed  { get; }   // true the frame the button is pressed
    bool    AttackPressed { get; }
    bool    DodgePressed  { get; }
}

// ── Real implementation (wraps Input System) ───────────────────────────────────

/// <summary>
/// Reads from a PlayerInputActions asset. Add to the player root alongside
/// PlayerInputHandler; register this into a service locator or inject via
/// Awake reference.
/// </summary>
public class LiveInputProvider : MonoBehaviour, IInputProvider
{
    private PlayerInputActions _actions;

    // Cached per-frame discrete events.
    private bool _jumpPressed;
    private bool _attackPressed;
    private bool _dodgePressed;

    public Vector2 MoveInput   { get; private set; }
    public Vector2 LookInput   { get; private set; }
    public bool    JumpPressed  => _jumpPressed;
    public bool    AttackPressed => _attackPressed;
    public bool    DodgePressed  => _dodgePressed;

    private void Awake() => _actions = new PlayerInputActions();

    private void OnEnable()
    {
        _actions.Player.Enable();
        _actions.Player.Jump.performed   += ctx => _jumpPressed   = true;
        _actions.Player.Attack.performed += ctx => _attackPressed = true;
        _actions.Player.Dodge.performed  += ctx => _dodgePressed  = true;
    }

    private void OnDisable()
    {
        _actions.Player.Disable();
    }

    private void OnDestroy() => _actions.Dispose();

    private void Update()
    {
        MoveInput = _actions.Player.Move.ReadValue<Vector2>();
        LookInput = _actions.Player.Look.ReadValue<Vector2>();
    }

    private void LateUpdate()
    {
        // Clear single-frame flags after all Update consumers have read them.
        _jumpPressed   = false;
        _attackPressed = false;
        _dodgePressed  = false;
    }
}

// ── AI / Test implementation ───────────────────────────────────────────────────

/// <summary>
/// Programmatic input provider for AI-controlled characters or unit tests.
/// Set values directly in code; gameplay components are unaffected.
/// </summary>
public class ScriptedInputProvider : IInputProvider
{
    public Vector2 MoveInput    { get; set; }
    public Vector2 LookInput    { get; set; }
    public bool    JumpPressed  { get; set; }
    public bool    AttackPressed { get; set; }
    public bool    DodgePressed  { get; set; }
}

// ── Consumer: PlayerMovement depends only on IInputProvider ───────────────────

/// <summary>
/// Moves the player. Does not know or care whether input comes from a device or AI.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [SerializeField] private float _speed    = 5f;
    [SerializeField] private float _jumpForce = 6f;
    [SerializeField] private float _gravity  = -9.81f;

    // Injected at startup by PlayerInputHandler or an AI controller.
    public IInputProvider Input { private get; set; }

    private CharacterController _cc;
    private float _verticalVelocity;

    private void Awake()  => _cc = GetComponent<CharacterController>();

    private void Update()
    {
        if (Input == null) return;

        if (_cc.isGrounded)
        {
            _verticalVelocity = Input.JumpPressed ? _jumpForce : -0.5f;
        }
        else
        {
            _verticalVelocity += _gravity * Time.deltaTime;
        }

        Vector3 move = new Vector3(Input.MoveInput.x, _verticalVelocity, Input.MoveInput.y);
        _cc.Move(move * _speed * Time.deltaTime);
    }

    // Called by PlayerInputHandler to set the direction from outside.
    public void SetMoveInput(Vector2 dir) { /* used if not injecting IInputProvider */ }
    public void SetLookInput(Vector2 dir) { /* used if not injecting IInputProvider */ }
    public void RequestJump()             { /* used if not injecting IInputProvider */ }
    public void RequestDodge()            { /* used if not injecting IInputProvider */ }
}
```

---

### Runtime Rebinding with Save/Load

```csharp
using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles runtime rebinding for a single InputAction.
/// Bind this to a UI button's onClick to start listening for a new binding.
/// Call SaveBindings() on confirmation; LoadBindings() on startup.
/// </summary>
public class InputRebinder : MonoBehaviour
{
    private const string BindingsKey = "InputBindingOverrides";

    [Tooltip("The InputActions asset — the same instance used by PlayerInputHandler.")]
    [SerializeField] private InputActionAsset _actionsAsset;

    private InputActionRebindingExtensions.RebindingOperation _rebindOp;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Begin interactive rebind for the specified action and binding index.
    /// </summary>
    /// <param name="actionName">Name as it appears in the .inputactions asset, e.g. "Player/Jump".</param>
    /// <param name="bindingIndex">Index of the binding to replace (0 for the first binding).</param>
    /// <param name="onComplete">Callback with the new binding display string.</param>
    /// <param name="onCancel">Callback invoked if the user presses Escape.</param>
    public void StartRebind(
        string actionName,
        int    bindingIndex,
        Action<string> onComplete,
        Action         onCancel = null)
    {
        InputAction action = _actionsAsset.FindAction(actionName);
        if (action == null)
        {
            Debug.LogWarning($"InputRebinder: action '{actionName}' not found.");
            return;
        }

        action.Disable(); // Must disable the action during rebinding.

        _rebindOp = action
            .PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>/position")
            .WithControlsExcluding("<Mouse>/delta")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(op =>
            {
                string display = action.GetBindingDisplayString(bindingIndex);
                CleanupRebind(action);
                onComplete?.Invoke(display);
            })
            .OnCancel(op =>
            {
                CleanupRebind(action);
                onCancel?.Invoke();
            })
            .Start();
    }

    /// <summary>Reset a binding to its default.</summary>
    public void ResetBinding(string actionName, int bindingIndex)
    {
        InputAction action = _actionsAsset.FindAction(actionName);
        if (action == null) return;

        action.RemoveBindingOverride(bindingIndex);
    }

    /// <summary>Persist all current overrides to PlayerPrefs.</summary>
    public void SaveBindings()
    {
        string json = _actionsAsset.SaveBindingOverridesAsJson();
        PlayerPrefs.SetString(BindingsKey, json);
        PlayerPrefs.Save();
    }

    /// <summary>Load persisted overrides from PlayerPrefs. Call on startup.</summary>
    public void LoadBindings()
    {
        if (!PlayerPrefs.HasKey(BindingsKey)) return;
        string json = PlayerPrefs.GetString(BindingsKey);
        _actionsAsset.LoadBindingOverridesFromJson(json);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void CleanupRebind(InputAction action)
    {
        _rebindOp?.Dispose();
        _rebindOp = null;
        action.Enable();
    }

    private void OnDestroy() => _rebindOp?.Dispose();
}
```

---

### Local Multiplayer with PlayerInputManager

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Manages local multiplayer player joining/leaving.
/// Requires a PlayerInputManager component on the same GameObject.
/// Configure PlayerPrefab and JoinBehavior in the PlayerInputManager inspector.
/// </summary>
[RequireComponent(typeof(PlayerInputManager))]
public class LocalMultiplayerCoordinator : MonoBehaviour
{
    [Header("UI feedback (optional)")]
    [SerializeField] private GameObject _joinPrompt;

    private PlayerInputManager _inputManager;
    private readonly List<PlayerController> _players = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        _inputManager = GetComponent<PlayerInputManager>();
    }

    private void OnEnable()
    {
        _inputManager.onPlayerJoined += HandlePlayerJoined;
        _inputManager.onPlayerLeft   += HandlePlayerLeft;
    }

    private void OnDisable()
    {
        _inputManager.onPlayerJoined -= HandlePlayerJoined;
        _inputManager.onPlayerLeft   -= HandlePlayerLeft;
    }

    // ── Joining / leaving ──────────────────────────────────────────────────────

    private void HandlePlayerJoined(PlayerInput playerInput)
    {
        // PlayerInput is on the spawned prefab root.
        if (!playerInput.TryGetComponent<PlayerController>(out PlayerController ctrl))
        {
            Debug.LogError("Player prefab missing PlayerController component.");
            return;
        }

        ctrl.Initialize(playerInput, _players.Count);
        _players.Add(ctrl);

        // Hide join prompt once minimum players have joined.
        if (_players.Count >= _inputManager.maxPlayerCount && _joinPrompt != null)
            _joinPrompt.SetActive(false);

        Debug.Log($"Player {_players.Count} joined with device: {playerInput.devices[0].displayName}");
    }

    private void HandlePlayerLeft(PlayerInput playerInput)
    {
        _players.RemoveAll(p => p.PlayerInput == playerInput);
        Debug.Log($"Player left. Remaining: {_players.Count}");
    }
}

/// <summary>
/// Player controller that accepts a PlayerInput reference at spawn time.
/// No singleton input access — each player has its own isolated InputActions.
/// </summary>
public class PlayerController : MonoBehaviour
{
    public PlayerInput PlayerInput { get; private set; }

    private int     _playerIndex;
    private Vector2 _moveInput;

    // ── Initialization (called by LocalMultiplayerCoordinator) ─────────────────

    public void Initialize(PlayerInput playerInput, int playerIndex)
    {
        PlayerInput  = playerInput;
        _playerIndex = playerIndex;

        // Wire up input callbacks from this player's isolated PlayerInput component.
        playerInput.actions["Player/Jump"].performed   += OnJump;
        playerInput.actions["Player/Attack"].performed += OnAttack;
    }

    private void OnDestroy()
    {
        if (PlayerInput == null) return;
        PlayerInput.actions["Player/Jump"].performed   -= OnJump;
        PlayerInput.actions["Player/Attack"].performed -= OnAttack;
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (PlayerInput == null) return;
        _moveInput = PlayerInput.actions["Player/Move"].ReadValue<Vector2>();
        // Apply movement...
    }

    private void OnJump(InputAction.CallbackContext ctx)
    {
        Debug.Log($"Player {_playerIndex} jumped");
    }

    private void OnAttack(InputAction.CallbackContext ctx)
    {
        Debug.Log($"Player {_playerIndex} attacked");
    }
}
```

---

### Device Change Detection (Icon Swapping)

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Detects when the active control scheme changes (e.g. player picks up a gamepad)
/// and notifies subscribers so UI can swap input hint icons.
/// Add to the player root alongside PlayerInput (set Notification to C# Events).
/// </summary>
[RequireComponent(typeof(PlayerInput))]
public class InputDeviceTracker : MonoBehaviour
{
    public static event System.Action<string> OnSchemeChanged;

    private PlayerInput _playerInput;

    private void Awake()  => _playerInput = GetComponent<PlayerInput>();

    private void OnEnable()  => _playerInput.onControlsChanged += HandleControlsChanged;
    private void OnDisable() => _playerInput.onControlsChanged -= HandleControlsChanged;

    private void HandleControlsChanged(PlayerInput input)
    {
        // currentControlScheme returns "Keyboard&Mouse" or "Gamepad", etc.
        OnSchemeChanged?.Invoke(input.currentControlScheme);
        Debug.Log($"Controls changed to: {input.currentControlScheme}");
    }
}
```

## Anti-Examples

### Input.GetKey() in New Projects

```csharp
// BAD — legacy API, not rebindable, no multi-device support, no action map context.
void Update()
{
    if (Input.GetKeyDown(KeyCode.Space))
        Jump();
    float h = Input.GetAxis("Horizontal");
    Move(h);
}

// GOOD — use the new Input System with a generated actions class.
// Binding is data-driven; game code never references a key name.
private void OnEnable()
{
    _actions.Player.Jump.performed += ctx => Jump();
}
private void Update()
{
    Move(_actions.Player.Move.ReadValue<Vector2>());
}
```

---

### Polling for Discrete Events (Jump, Attack)

```csharp
// BAD — polls WasPressedThisFrame every Update.
// If Update is skipped (low FPS, frame spike) the press is lost.
// Also mixes polling and event patterns, making code harder to follow.
void Update()
{
    if (Keyboard.current.spaceKey.wasPressedThisFrame)
        Jump();
}

// GOOD — subscribe to action.performed; it fires exactly once per press
// regardless of frame rate.
private void OnEnable() => _actions.Player.Jump.performed += ctx => Jump();
```

---

### Hardcoded Key Bindings

```csharp
// BAD — WASD hardcoded in movement logic.
// Player cannot rebind; no gamepad support; changing keys requires code edits.
void Update()
{
    float x = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
    float z = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
    Move(new Vector2(x, z));
}

// GOOD — a single "Move" action with WASD and left-stick bindings in the asset.
// Rebinding at runtime changes the binding; game code is unchanged.
```

---

### Single Action Map for Everything

```csharp
// BAD — one action map that is always enabled.
// UI "Submit" fires during gameplay; "Attack" fires on menus.
// No way to disable gameplay input when a menu is open.

// GOOD — separate Player and UI action maps.
// Disable Player map when opening a menu; the UI map stays active.
private void OpenMenu()
{
    _actions.Player.Disable();
    _actions.UI.Enable();
}
private void CloseMenu()
{
    _actions.UI.Disable();
    _actions.Player.Enable();
}
```

## Cross-References

- Related skills: `hades:component-design`, `hades:unity-ui` (for rebinding UI)
- Hades MCP tools: `search_by_name`, `find_components_using_pattern`, `recall_memory`, `propose_memory_update`
- Unity docs: [Input System package](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.8/manual/index.html), [PlayerInput component](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.8/manual/PlayerInput.html), [PlayerInputManager](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.8/manual/PlayerInputManager.html), [Runtime rebinding](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.8/manual/ActionBindings.html#interactive-rebinding)
