---
description: "Use when setting up animations — Animator Controllers, Animation Clips, state machines, blend trees, animation events, and Avatar configuration."
---

# Animation Workflow

Procedural guide for setting up Unity animation systems. Covers Animator Controller authoring, state machine design, blend tree configuration, animation events, and Avatar/humanoid rigging — all via the `UnityEditor.Animations` API and the Unity Animator runtime.

## When to Apply

Activate when the task involves:
- Creating or editing an Animator Controller asset
- Defining animation states, transitions, and parameters
- Setting up blend trees (1D, 2D Freeform, 2D Cartesian)
- Configuring animation events that call MonoBehaviour methods
- Setting up Avatar definitions for humanoid characters
- Adding animation layers for body part isolation (upper body, additive)
- Writing editor utilities to scaffold a standard controller from code

Do NOT activate for clip authoring or rigging in a 3D DCC tool (Blender, Maya) — that work happens outside Unity. Return here once imported clips need to be wired into a controller.

## Project Context Check

Before creating or modifying any animation asset:

1. **Inventory existing animation assets:**
   - Call `search_by_name("*.controller")` to find all Animator Controllers — avoid creating a duplicate for a character that already has one
   - Call `search_by_name("*.anim")` to see which animation clips are already imported and available
   - Call `find_components_using_pattern("Animator")` to identify which GameObjects/prefabs have an Animator component — this surfaces the characters and props that need controllers wired up
   - Call `recall_memory("animation workflow conventions")` to surface documented team conventions (layer naming, parameter naming, state naming, root motion policy)

2. **Adapt work based on findings:**
   - If a controller already exists for the target character → open and extend it; do not create a parallel asset
   - If memory documents a parameter naming convention (e.g. `Speed`, `IsGrounded`, `AttackTrigger`) → follow it exactly so gameplay code that sets those parameters keeps working
   - If root motion is disabled project-wide → do not enable Apply Root Motion without discussing with the user
   - After establishing a new controller pattern, call `propose_memory_update` to record the state machine structure

## Decision Framework

### Which state machine structure fits the character?

```
How many discrete locomotion states does the character need?
├── 1–3 states (Idle, Walk, Run)
│   └── Linear 1D Blend Tree on a Speed parameter — simplest
├── 4–8 states including directional variants (8-directional strafe)
│   └── 2D Freeform Directional Blend Tree on MoveX/MoveZ parameters
└── Complex (combat, platformer, vehicle)
    └── Layered state machine:
        - Base Layer: locomotion blend tree
        - Combat Layer (override): attack, hit, death states
        - Upper Body Layer (additive): aim, recoil states
```

---

### Animator vs legacy Animation component?

```
Is this a new character or prop?
├── Yes → Use Animator + Animator Controller. Always.
└── No — inheriting a legacy Animation component?
    ├── Active gameplay object → migrate to Animator now
    └── One-off environment prop (e.g. a simple door) → legacy is acceptable
        but do not introduce new Animation components on gameplay objects
```

**Never add the legacy `Animation` component to new gameplay objects.** It lacks state machine, blending, and layering support and has no upgrade path.

---

### Transition conditions: bool vs trigger vs int/float?

| Parameter type | Use when | Caution |
|---------------|----------|---------|
| `bool` | State that persists (IsGrounded, IsAiming) | Must be reset explicitly — easy to get stuck in wrong state |
| `trigger` | One-shot events (Attack, Jump, TakeDamage) | Can be consumed before the transition fires if set too early |
| `float` | Blend tree inputs (Speed, MoveX, MoveZ) | Keep 0–1 normalised for blend trees |
| `int` | Enumerated states (WeaponType 0/1/2) | Keep the int–state mapping documented in memory |

Avoid complex multi-condition transitions. A transition with more than two conditions is hard to debug when it fails to fire. Prefer one condition per transition, and let the state machine structure encode the ordering.

---

### Root motion policy?

```
Does the animation clip contain root motion (position/rotation baked into root bone)?
├── Yes
│   ├── Character uses a CharacterController or Rigidbody for movement?
│   │   └── Disable Apply Root Motion — drive movement from code; extract root motion if needed
│   └── Character is entirely animation-driven (cinematic, NPC)?
│       └── Enable Apply Root Motion
└── No (in-place animation)
    └── Disable Apply Root Motion (default)
```

## Code Examples

### CreateAnimatorController — Editor Utility

Creates an Animator Controller with the standard locomotion and combat state structure. Use as a scaffold for new characters.

```csharp
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Editor utility: Tools > Hades > Create Standard Animator Controller
/// Creates a controller asset with Idle/Walk/Run/Jump locomotion states
/// and a combat trigger-based attack state on an override layer.
/// </summary>
public static class CreateAnimatorController
{
    private const string DefaultSavePath = "Assets/Animations/Controllers/";

    [MenuItem("Tools/Hades/Create Standard Animator Controller")]
    public static void Execute()
    {
        string savePath = EditorUtility.SaveFilePanelInProject(
            title:       "Save Animator Controller",
            defaultName: "NewCharacterController",
            extension:   "controller",
            message:     "Choose location for the new Animator Controller",
            path:        DefaultSavePath
        );

        if (string.IsNullOrEmpty(savePath)) return;

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(savePath);

        // ── Parameters ────────────────────────────────────────────────────────
        controller.AddParameter("Speed",      AnimatorControllerParameterType.Float);
        controller.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
        controller.AddParameter("Jump",       AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Attack",     AnimatorControllerParameterType.Trigger);
        controller.AddParameter("TakeDamage", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Die",        AnimatorControllerParameterType.Trigger);

        // ── Base Layer: Locomotion ─────────────────────────────────────────────
        AnimatorStateMachine baseLayer = controller.layers[0].stateMachine;
        baseLayer.name = "Locomotion";

        AnimatorState locomotionBlend = AddBlendTree(controller, baseLayer, "Locomotion Blend");
        AnimatorState jumpState       = AddState(baseLayer, "Jump",    position: new Vector3(400, 100));
        AnimatorState deathState      = AddState(baseLayer, "Death",   position: new Vector3(400, 300));

        // Locomotion → Jump
        var toJump = locomotionBlend.AddTransition(jumpState);
        toJump.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        toJump.hasExitTime = false;
        toJump.duration    = 0.1f;

        // Jump → Locomotion (on exit time)
        var jumpBack = jumpState.AddTransition(locomotionBlend);
        jumpBack.hasExitTime  = true;
        jumpBack.exitTime     = 0.9f;
        jumpBack.duration     = 0.2f;

        // Any State → Death
        var toDeath = baseLayer.AddAnyStateTransition(deathState);
        toDeath.AddCondition(AnimatorConditionMode.If, 0, "Die");
        toDeath.hasExitTime = false;
        toDeath.duration    = 0.15f;

        baseLayer.defaultState = locomotionBlend;

        // ── Combat Layer: Override ─────────────────────────────────────────────
        AddCombatLayer(controller);

        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(controller);
        Debug.Log($"[CreateController] Saved to '{savePath}'.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AnimatorState AddState(AnimatorStateMachine sm, string name, Vector3 position)
    {
        AnimatorState state = sm.AddState(name, position);
        // Clip assignment: drag the correct clip in the inspector after creation.
        return state;
    }

    private static AnimatorState AddBlendTree(
        AnimatorController controller,
        AnimatorStateMachine sm,
        string name)
    {
        AnimatorState state = sm.AddState(name, new Vector3(200, 0));
        BlendTree blendTree;
        AnimatorState result = controller.CreateBlendTreeInController(name, out blendTree);

        // Attach the blend tree to the state.
        state.motion = blendTree;

        blendTree.blendType      = BlendTreeType.Simple1D;
        blendTree.blendParameter = "Speed";

        // Leaf motions — assign actual clips via inspector or extend this code.
        blendTree.AddChild(null, 0f);  // Idle  — assign IdleClip
        blendTree.AddChild(null, 0.5f); // Walk  — assign WalkClip
        blendTree.AddChild(null, 1f);  // Run   — assign RunClip

        return state;
    }

    private static void AddCombatLayer(AnimatorController controller)
    {
        AnimatorControllerLayer combatLayer = new AnimatorControllerLayer
        {
            name            = "Combat",
            defaultWeight   = 1f,
            blendingMode    = AnimatorLayerBlendingMode.Override,
            stateMachine    = new AnimatorStateMachine
            {
                name     = "Combat",
                hideFlags = HideFlags.HideInHierarchy,
            },
        };

        // Persist the nested state machine as a sub-asset.
        AssetDatabase.AddObjectToAsset(combatLayer.stateMachine, controller);

        controller.AddLayer(combatLayer);

        AnimatorStateMachine sm = combatLayer.stateMachine;
        AnimatorState idle   = sm.AddState("Idle Override", new Vector3(200, 0));
        AnimatorState attack = sm.AddState("Attack",        new Vector3(400, 0));
        AnimatorState hit    = sm.AddState("Hit",           new Vector3(400, 200));

        sm.defaultState = idle;

        var toAttack = idle.AddTransition(attack);
        toAttack.AddCondition(AnimatorConditionMode.If, 0, "Attack");
        toAttack.hasExitTime = false;
        toAttack.duration    = 0.05f;

        var attackBack = attack.AddTransition(idle);
        attackBack.hasExitTime = true;
        attackBack.exitTime    = 0.9f;
        attackBack.duration    = 0.1f;

        var toHit = idle.AddTransition(hit);
        toHit.AddCondition(AnimatorConditionMode.If, 0, "TakeDamage");
        toHit.hasExitTime = false;
        toHit.duration    = 0.05f;

        var hitBack = hit.AddTransition(idle);
        hitBack.hasExitTime = true;
        hitBack.exitTime    = 0.9f;
        hitBack.duration    = 0.1f;
    }
}
```

---

### Animation Event Receiver — MonoBehaviour Pattern

Animation events call a named method on a MonoBehaviour on the same GameObject as the Animator. This pattern makes the receiver explicit and avoids stale-reference crashes.

```csharp
using UnityEngine;

/// <summary>
/// Receives animation events fired from clips playing on this character's Animator.
/// Add this component to the same GameObject as the Animator.
///
/// To wire an event: select the clip in the Animation window, move to the desired
/// frame, click "Add Event", and set the Function field to the method name.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterAnimationEvents : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private AudioSource _audioSource;
    [SerializeField] private AudioClip   _footstepClip;
    [SerializeField] private AudioClip   _attackSwingClip;
    [SerializeField] private AudioClip   _landClip;

    [Header("VFX")]
    [SerializeField] private ParticleSystem _attackTrailVfx;
    [SerializeField] private ParticleSystem _landDustVfx;

    // ── Event receivers (called by name from animation clips) ─────────────────

    /// <summary>Frame-accurate footstep audio. Fire on foot-contact frames.</summary>
    public void OnFootstep()
    {
        if (_audioSource == null || _footstepClip == null) return;
        _audioSource.PlayOneShot(_footstepClip);
    }

    /// <summary>Triggers the attack hitbox check window. Fire at the swing apex.</summary>
    public void OnAttackHitboxOpen()
    {
        // Notify Combat component to activate its hitbox for this frame window.
        if (TryGetComponent<Combat>(out Combat combat))
            combat.BeginHitboxWindow();
    }

    /// <summary>Closes the hitbox window after the impact frame.</summary>
    public void OnAttackHitboxClose()
    {
        if (TryGetComponent<Combat>(out Combat combat))
            combat.EndHitboxWindow();
    }

    /// <summary>Attack audio and VFX. Fire at the start of the swing.</summary>
    public void OnAttackSwing()
    {
        if (_audioSource != null && _attackSwingClip != null)
            _audioSource.PlayOneShot(_attackSwingClip);

        if (_attackTrailVfx != null)
            _attackTrailVfx.Play();
    }

    /// <summary>Landing impact. Fire on the landing frame of a jump recovery clip.</summary>
    public void OnLand()
    {
        if (_audioSource != null && _landClip != null)
            _audioSource.PlayOneShot(_landClip);

        if (_landDustVfx != null)
            _landDustVfx.Play();
    }
}
```

---

### State Machine Driver — Gameplay Code Pattern

Bridges gameplay inputs/state to Animator parameters. Centralises all Animator.Set* calls in one component, avoiding scattered animator.SetFloat calls across unrelated scripts.

```csharp
using UnityEngine;

/// <summary>
/// Drives the character's Animator parameters from gameplay state each frame.
/// Centralises all Animator.Set* calls — no other component should write to
/// this character's Animator directly.
/// </summary>
[RequireComponent(typeof(Animator))]
public class CharacterAnimatorDriver : MonoBehaviour
{
    // ── Cached parameter hashes ───────────────────────────────────────────────
    // Use Animator.StringToHash at startup — avoids per-frame string hashing.
    private static readonly int SpeedHash      = Animator.StringToHash("Speed");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
    private static readonly int JumpHash       = Animator.StringToHash("Jump");
    private static readonly int AttackHash     = Animator.StringToHash("Attack");
    private static readonly int TakeDamageHash = Animator.StringToHash("TakeDamage");
    private static readonly int DieHash        = Animator.StringToHash("Die");

    [SerializeField] private float _speedSmoothTime = 0.1f;

    private Animator   _animator;
    private Movement   _movement;
    private Health     _health;
    private float      _speedVelocity; // used by SmoothDamp

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _movement = GetComponent<Movement>();
        _health   = GetComponent<Health>();

        if (_health != null)
        {
            _health.OnDied.AddListener(OnDied);
        }
    }

    private void Update()
    {
        if (_health != null && _health.IsDead) return;

        // Speed — smooth the value to avoid abrupt blend tree transitions.
        float targetSpeed = _movement != null ? _movement.NormalisedSpeed : 0f;
        float currentSpeed = _animator.GetFloat(SpeedHash);
        float smoothedSpeed = Mathf.SmoothDamp(
            current: currentSpeed,
            target:  targetSpeed,
            currentVelocity: ref _speedVelocity,
            smoothTime: _speedSmoothTime
        );
        _animator.SetFloat(SpeedHash, smoothedSpeed);
    }

    // ── Public API (called by input handler, AI, or combat system) ────────────

    public void SetGrounded(bool isGrounded) =>
        _animator.SetBool(IsGroundedHash, isGrounded);

    public void TriggerJump() =>
        _animator.SetTrigger(JumpHash);

    public void TriggerAttack() =>
        _animator.SetTrigger(AttackHash);

    public void TriggerTakeDamage() =>
        _animator.SetTrigger(TakeDamageHash);

    // ── Event listeners ───────────────────────────────────────────────────────

    private void OnDied()
    {
        _animator.SetTrigger(DieHash);
        // Disable further parameter updates.
        enabled = false;
    }
}
```

---

### Avatar Setup Check — Editor Utility

Validates that a humanoid rig is configured correctly before wiring it into a controller.

```csharp
using UnityEditor;
using UnityEngine;

/// <summary>
/// Validates the Avatar configuration for a selected model import.
/// Run via Tools menu when importing a new humanoid character.
/// </summary>
public static class ValidateAvatarSetup
{
    [MenuItem("Tools/Hades/Validate Selected Avatar")]
    public static void Execute()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            Debug.LogWarning("[ValidateAvatar] Select a model asset or prefab first.");
            return;
        }

        Animator animator = selected.GetComponent<Animator>();
        if (animator == null)
        {
            Debug.LogError("[ValidateAvatar] No Animator found on selected object.");
            return;
        }

        Avatar avatar = animator.avatar;
        if (avatar == null)
        {
            Debug.LogError("[ValidateAvatar] Animator has no Avatar assigned.");
            return;
        }

        if (!avatar.isValid)
        {
            Debug.LogError($"[ValidateAvatar] Avatar '{avatar.name}' is invalid.");
            return;
        }

        if (!avatar.isHuman)
        {
            Debug.LogWarning($"[ValidateAvatar] Avatar '{avatar.name}' is Generic (not Humanoid). " +
                             "Humanoid retargeting and IK will not be available.");
        }
        else
        {
            Debug.Log($"[ValidateAvatar] Avatar '{avatar.name}' is valid Humanoid.");
        }

        // Check the model importer rig settings.
        string path = AssetDatabase.GetAssetPath(selected);
        if (!string.IsNullOrEmpty(path))
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                Debug.Log($"[ValidateAvatar] Animation type: {importer.animationType}");
                Debug.Log($"[ValidateAvatar] Optimise game objects: {importer.optimizeGameObjects}");
                if (importer.optimizeGameObjects)
                    Debug.LogWarning("[ValidateAvatar] Optimise Game Objects is ON — bones are hidden. " +
                                     "Disable if you need to access bone transforms at runtime.");
            }
        }
    }
}
```

### Alternative: Direct MCP Tool Calls

If you prefer tool calls over scripting, these editor-action tools are available:
- `animation_create_controller` — create an AnimatorController asset
- `animation_edit_controller` — modify controller layers, states, transitions, and parameters
- `animation_assign_controller` — assign a controller to an Animator component
- `animation_assign_clip` — assign an AnimationClip to a state
- `animation_get_controller` — inspect controller structure (layers, states, parameters)

Choose tools for quick one-off operations. Choose C# scripting (AnimatorController API) for reusable Editor tools or complex batch operations.

## Anti-Examples

### Using the Legacy Animation Component for New Characters

```csharp
// BAD — adding the legacy Animation component on a new gameplay character.
//
// var anim = gameObject.AddComponent<Animation>();
// anim.Play("Run");
//
// The legacy Animation component predates Unity 4's Mecanim system.
// It has no state machine, no blend trees, no layers, no IK, and no Animator
// parameter API. Scripts that call anim.Play() by name string are fragile —
// rename the clip and the code silently stops working.
//
// Always use Animator + AnimatorController for new characters.
```

---

### Complex Multi-Condition Transitions

```csharp
// BAD — a transition with four conditions that fires only in a specific combination:
//
// transition.AddCondition(AnimatorConditionMode.Greater,  0.5f,  "Speed");
// transition.AddCondition(AnimatorConditionMode.If,       0,     "IsGrounded");
// transition.AddCondition(AnimatorConditionMode.IfNot,    0,     "IsAiming");
// transition.AddCondition(AnimatorConditionMode.Greater,  0,     "StaminaRatio");
//
// Debugging why this transition does not fire requires satisfying four simultaneous
// conditions. A single mistaken state in any one parameter blocks the entire transition.
//
// Prefer: one condition per transition, and model ordering through the state machine
// structure (add intermediate states, use Any State sparingly).
```

---

### Animation Events Calling Methods on Destroyed Objects

```csharp
// BAD — an animation clip fires an event that calls a method on a component
// that may have been destroyed (e.g. the character died mid-attack animation).
//
// public void OnAttackHitboxOpen()
// {
//     _combat.BeginHitboxWindow();  // NullReferenceException if _combat was destroyed
// }
//
// Fix: always null-check the target, and disable the AnimationEvents receiver
// component (or the Animator itself) when the object's lifetime ends.
//
// public void OnAttackHitboxOpen()
// {
//     if (_combat == null) return;
//     _combat.BeginHitboxWindow();
// }
//
// Also disable CharacterAnimationEvents in OnDied() so no events fire
// during the death animation.
```

---

### Setting Animator Parameters by String Each Frame

```csharp
// BAD — calling Animator.SetFloat with a raw string every Update.
//
// void Update()
// {
//     _animator.SetFloat("Speed", _movement.speed);     // string hash lookup every frame
//     _animator.SetBool("IsGrounded", _isGrounded);     // string hash lookup every frame
// }
//
// Animator.StringToHash is called on every Set* call when given a string.
// At 60 fps with 50 animated characters this is 6,000 unnecessary hash computations
// per second.
//
// Fix: cache hashes as static readonly int fields at class level (as shown in
// CharacterAnimatorDriver above) and pass the int overload instead.
```

## Cross-References

- Architecture decisions before animating: `hades:unity-architect`, `hades:component-design`
- Scene and prefab setup: `hades:scene-authoring`, `hades:prefab-workflow`
- Hades MCP tools used in this skill: `search_by_name`, `find_components_using_pattern`, `recall_memory`, `propose_memory_update`, `animation_create_controller`, `animation_edit_controller`, `animation_assign_controller`, `animation_assign_clip`, `animation_get_controller`
- Unity docs: [Animator Controller](https://docs.unity3d.com/6000.0/Documentation/Manual/class-AnimatorController.html), [Blend Trees](https://docs.unity3d.com/6000.0/Documentation/Manual/class-BlendTree.html), [Animation Events](https://docs.unity3d.com/6000.0/Documentation/Manual/animeditor-AnimationEvents.html), [Avatar](https://docs.unity3d.com/6000.0/Documentation/Manual/ConfiguringtheAvatar.html), [AnimatorControllerLayer](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AnimatorControllerLayer.html)
