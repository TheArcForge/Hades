---
description: "Use when building user interfaces — UI Toolkit vs uGUI decisions, layout strategies, dialog systems, responsive UI, data binding, and HUD architecture."
---

# Unity UI

Guidance for building Unity interfaces: choosing between UI Toolkit and uGUI, laying out flexible screens, wiring data to views, managing dialog stacks, and structuring persistent HUDs.

## When to Apply

Activate when the conversation involves:
- Deciding whether to use UI Toolkit or uGUI for a new screen or feature
- Laying out panels, lists, or HUD elements that must adapt to different resolutions
- Creating dialog, popup, or modal systems with open/close navigation
- Binding runtime data (health, score, inventory) to visible UI elements
- Structuring a persistent HUD overlay that receives frequent updates
- Reviewing a UI script that has become large or is updating every frame unnecessarily
- Setting up UXML/USS templates or Canvas prefabs for reuse

Do NOT activate for questions purely about animations on UI elements (see `hades:animation-workflow`), 3D world-space labels attached to game objects, or input handling that only happens to trigger UI (see `hades:unity-input`).

## Project Context Check

Before making recommendations:

1. **Detect what UI system is already in use:**
   - Call `find_components_using_pattern("UIDocument")` to see if UI Toolkit documents are present
   - Call `find_components_using_pattern("Canvas")` to see if uGUI canvases exist
   - Call `search_by_name("*.uxml")` to find UXML templates already authored
   - Call `search_by_name("*.uss")` to find USS stylesheets in the project

2. **Check recorded decisions in memory:**
   - Call `recall_memory("UI architecture toolkit uGUI")` to find any documented UI strategy
   - If a recalled decision shows `warning`, surface the conflict to the user before proceeding

3. **Adapt recommendations based on findings:**
   - If both `UIDocument` and `Canvas` are present → the project is mixed; establish which system owns which surface before adding more UI
   - If only `Canvas` is found → recommend uGUI unless there is a strong reason to introduce UI Toolkit
   - If only `UIDocument` is found → stay in UI Toolkit; do not introduce uGUI without a clear reason (e.g., world-space)
   - After the user agrees on an approach not yet documented, call `propose_memory_update` to record the decision

## Decision Framework

### UI Toolkit vs uGUI

```
Is this Unity 6+ and runtime (not Editor tooling)?
├── Yes → UI Toolkit is viable; continue below
└── No  → Use uGUI (UI Toolkit runtime was unstable before Unity 6)

Does the UI need to appear in world space (e.g., floating health bar above enemy)?
├── Yes → Use uGUI Canvas (World Space render mode)
│         UI Toolkit has no stable world-space render path
└── No  → Continue below

Does the UI require heavy animation (tweens, keyframed transitions, sprite sheets)?
├── Yes → uGUI + Animator or DOTween is more mature
│         UI Toolkit transitions are CSS-like and limited
└── No  → Continue below

Is the project already using UI Toolkit for runtime UI?
├── Yes → Stay in UI Toolkit for consistency
└── No  → Consider uGUI if:
          - Team is more familiar with it
          - Third-party asset store UI packs are needed
          - Significant uGUI code already exists in the project
          Otherwise → UI Toolkit for new projects (better CSS-like styling, data binding)
```

**Summary table:**

| Factor | UI Toolkit | uGUI |
|--------|-----------|------|
| Editor tooling | Best choice | Avoid |
| Runtime UI, Unity 6+ | Preferred | Also fine |
| World-space UI | Not supported | Required |
| Heavy keyframed animation | Limited | Mature |
| Asset store UI packs | Few available | Many available |
| CSS-like styling | Yes (USS) | No |
| First-party data binding | Yes (BindingPath) | No (manual) |
| Screen-space overlay | Both fine | Both fine |

---

### Layout Strategies

**UI Toolkit (Flexbox)**

UI Toolkit uses a subset of CSS Flexbox. Key properties in USS:

- `flex-direction: row | column` — axis for child layout
- `flex-wrap: wrap | nowrap` — allow children to wrap to next line
- `flex-grow: 1` — child expands to fill remaining space
- `align-items: center | flex-start | flex-end | stretch`
- `justify-content: space-between | center | flex-start`

Use `%` units for widths/heights that must be proportional to parent. Use `px` only for fixed chrome (icons, borders).

**uGUI (Layout Groups)**

- `HorizontalLayoutGroup` / `VerticalLayoutGroup`: child-driven; set `Child Force Expand` flags carefully — enabling both width and height expands all children equally, which is rarely desired
- `GridLayoutGroup`: fixed cell size; use for icon grids, inventory slots
- `ContentSizeFitter`: makes the rect grow to fit its content; pair with a Layout Group for dynamic lists
- `LayoutElement`: override min/preferred/flexible sizes on individual children

For multi-resolution support, add a `CanvasScaler` set to **Scale with Screen Size** using a reference resolution (e.g., 1920×1080) and **Match** slider at 0.5 (balanced width/height matching).

---

### Dialog / Popup Systems

Use a stack-based dialog manager. Rules:

1. Only the top-of-stack dialog is interactive; everything below is blocked
2. Opening pushes to the stack; closing pops — never call Close directly on a dialog; always route through the manager
3. Dialogs are prefabs instantiated into a dedicated Dialog Root canvas or VisualElement container so they layer above the HUD
4. The manager raises events so the HUD or game state can react (e.g., pause when any dialog is open)

---

### HUD Architecture

A HUD is a persistent overlay updated by live game data. Key rules:

- **Dirty-flag updates only** — do NOT poll or rebuild the entire HUD every frame. Update UI only when the underlying value changes.
- Use C# events or SO event channels to push changes from game systems to the HUD (see `hades:component-design`).
- Keep the HUD MonoBehaviour (or UIDocument controller) responsible only for reading data and writing to UI elements — no game logic.
- For UI Toolkit: subscribe to `INotifyValueChanged<T>` or use `BindingPath` to auto-sync data; avoid reading `panel.rootVisualElement` every frame.
- For uGUI: cache `Text`, `Image`, and `Slider` references in `Awake`; update them in event callbacks, not in `Update`.

---

### Data Binding (UI Toolkit)

UI Toolkit supports two binding approaches:

1. **BindingPath + SerializedObject** (editor and simple runtime cases): set `BindingPath` on a field in UXML; call `Bind(serializedObject)` on the root element. Works well for inspector-style tools.
2. **INotifyValueChanged<T>** (runtime): register `RegisterValueChangedCallback` on controls; respond to changes without polling.
3. **Custom binding (Unity 6+)**: implement `IDataSource` or use the `DataBinding` API to connect any plain C# object to a VisualElement property path. Preferred for MVVM patterns.

---

### Screen Adaptation

- **Safe areas**: on mobile, offset UI content by `Screen.safeArea`. In uGUI, add a `SafeAreaPanel` script that reads `Screen.safeArea` and sets its RectTransform anchors and offsets each time the screen orientation changes.
- **Aspect ratio**: for fixed-aspect content (e.g., a 16:9 game view letterboxed on a 4:3 device), use an `AspectRatioFitter` on the root panel.
- **Canvas Scaler modes**:
  - `Constant Pixel Size` — no scaling; use only for pixel-art projects
  - `Scale with Screen Size` — recommended for most projects
  - `Constant Physical Size` — use when physical size matters (e.g., button must be 1cm on any device)

## Code Examples

### UI Toolkit: UXML + USS + C# Controller

Complete health bar implemented as a UI Toolkit VisualElement with USS styling and a C# MonoBehaviour controller.

**HealthBar.uxml**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements">
    <Style src="HealthBar.uss" />
    <ui:VisualElement name="health-bar-root" class="health-bar-root">
        <ui:VisualElement name="health-bar-fill" class="health-bar-fill" />
        <ui:Label name="health-label" class="health-label" text="100 / 100" />
    </ui:VisualElement>
</ui:UXML>
```

**HealthBar.uss**
```css
.health-bar-root {
    width: 200px;
    height: 20px;
    background-color: rgb(40, 40, 40);
    border-radius: 4px;
    overflow: hidden;
    position: relative;
}

.health-bar-fill {
    position: absolute;
    top: 0;
    left: 0;
    bottom: 0;
    width: 100%;
    background-color: rgb(80, 200, 80);
    transition-property: width;
    transition-duration: 0.2s;
    transition-timing-function: ease-out;
}

.health-label {
    position: absolute;
    width: 100%;
    height: 100%;
    -unity-text-align: middle-center;
    color: white;
    font-size: 11px;
}
```

**HealthBarController.cs**
```csharp
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives a UI Toolkit health bar from a PlayerHealth component.
/// Subscribes to change events — never polls in Update.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class HealthBarController : MonoBehaviour
{
    [SerializeField] private PlayerHealth _playerHealth;

    private VisualElement _fill;
    private Label _label;

    private void Awake()
    {
        UIDocument doc = GetComponent<UIDocument>();
        VisualElement root = doc.rootVisualElement;

        _fill  = root.Q<VisualElement>("health-bar-fill");
        _label = root.Q<Label>("health-label");
    }

    private void OnEnable()
    {
        if (_playerHealth != null)
            _playerHealth.OnHealthChanged += HandleHealthChanged;
    }

    private void OnDisable()
    {
        if (_playerHealth != null)
            _playerHealth.OnHealthChanged -= HandleHealthChanged;
    }

    private void HandleHealthChanged(float current, float max)
    {
        float fraction = max > 0f ? Mathf.Clamp01(current / max) : 0f;
        // width as % so it works at any container size
        _fill.style.width = Length.Percent(fraction * 100f);
        _label.text = $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(max)}";
    }
}
```

---

### UI Toolkit: Custom VisualElement (Radial Cooldown Indicator)

A self-contained VisualElement subclass that draws itself with the MeshGenerationContext API.

```csharp
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Draws a clock-wipe cooldown arc. Add to UXML as:
/// &lt;RadialCooldown name="ability-cooldown" style="width:64px; height:64px;" /&gt;
/// Then set Progress (0–1) from code to update the fill.
/// </summary>
public class RadialCooldown : VisualElement
{
    // Expose to UXML factory
    public new class UxmlFactory : UxmlFactory<RadialCooldown, UxmlTraits> { }
    public new class UxmlTraits : VisualElement.UxmlTraits { }

    private float _progress;

    /// <summary>Fraction filled, 0 = empty, 1 = full. Setting this triggers a repaint.</summary>
    public float Progress
    {
        get => _progress;
        set
        {
            _progress = Mathf.Clamp01(value);
            MarkDirtyRepaint();
        }
    }

    public RadialCooldown()
    {
        generateVisualContent += OnGenerateVisualContent;
    }

    private void OnGenerateVisualContent(MeshGenerationContext ctx)
    {
        Rect rect = contentRect;
        Vector2 center = rect.center;
        float radius = Mathf.Min(rect.width, rect.height) * 0.45f;

        var painter = ctx.painter2D;

        // Background ring
        painter.strokeColor = new Color(0.15f, 0.15f, 0.15f, 0.8f);
        painter.lineWidth = 6f;
        painter.BeginPath();
        painter.Arc(center, radius, 0f, 360f);
        painter.Stroke();

        // Cooldown fill arc (sweeps from top, clockwise)
        if (_progress > 0f)
        {
            float endAngle = -90f + _progress * 360f;
            painter.strokeColor = new Color(0.2f, 0.7f, 1f, 1f);
            painter.lineWidth = 6f;
            painter.BeginPath();
            painter.Arc(center, radius, -90f, endAngle, ArcDirection.Clockwise);
            painter.Stroke();
        }
    }
}
```

---

### uGUI: Canvas Setup with CanvasScaler

Correctly configured screen-space overlay Canvas for a multi-resolution project.

```csharp
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bootstraps a screen-space overlay Canvas at runtime with proper CanvasScaler settings.
/// Alternatively create this in the Editor and save as a prefab — this class shows the
/// configuration values to use.
/// </summary>
public class HUDCanvasBootstrap : MonoBehaviour
{
    [SerializeField] private Vector2 _referenceResolution = new Vector2(1920f, 1080f);

    /// <summary>
    /// 0 = match width only, 1 = match height only, 0.5 = balanced.
    /// 0.5 is a safe default for most games.
    /// </summary>
    [SerializeField, Range(0f, 1f)] private float _matchWidthOrHeight = 0.5f;

    private Canvas _canvas;
    private CanvasScaler _scaler;

    private void Awake()
    {
        _canvas = GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100; // Above world-space canvases

        _scaler = GetComponent<CanvasScaler>();
        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _scaler.referenceResolution = _referenceResolution;
        _scaler.matchWidthOrHeight = _matchWidthOrHeight;

        // Ensure a GraphicRaycaster is present for button interactions
        if (!TryGetComponent<GraphicRaycaster>(out _))
            gameObject.AddComponent<GraphicRaycaster>();
    }
}
```

---

### Dialog Manager (Stack-Based, Full Implementation)

Manages a stack of dialog prefabs; only the top dialog is interactive.

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages a stack of UI dialogs. Push to open; Pop to close the top dialog.
/// Dialogs are instantiated into DialogRoot. The game should pause or disable
/// input when IsDialogOpen returns true.
/// </summary>
public class DialogManager : MonoBehaviour
{
    public static DialogManager Instance { get; private set; }

    [Tooltip("Parent transform where dialog prefabs are instantiated.")]
    [SerializeField] private Transform _dialogRoot;

    private readonly Stack<GameObject> _stack = new();

    public bool IsDialogOpen => _stack.Count > 0;

    public event System.Action<bool> OnDialogStateChanged;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Instantiates <paramref name="dialogPrefab"/> and pushes it onto the stack.
    /// The previously visible dialog is disabled (not destroyed) while covered.
    /// </summary>
    public GameObject Push(GameObject dialogPrefab)
    {
        // Disable current top so it cannot receive input while covered
        if (_stack.TryPeek(out GameObject current))
            current.SetActive(false);

        GameObject instance = Instantiate(dialogPrefab, _dialogRoot);
        _stack.Push(instance);

        OnDialogStateChanged?.Invoke(true);
        return instance;
    }

    /// <summary>
    /// Destroys the top dialog and re-enables the one beneath it (if any).
    /// Safe to call when the stack is empty.
    /// </summary>
    public void Pop()
    {
        if (_stack.Count == 0) return;

        Destroy(_stack.Pop());

        if (_stack.TryPeek(out GameObject previous))
            previous.SetActive(true);

        OnDialogStateChanged?.Invoke(_stack.Count > 0);
    }

    /// <summary>Destroys all dialogs and resets the stack.</summary>
    public void PopAll()
    {
        while (_stack.Count > 0)
            Destroy(_stack.Pop());

        OnDialogStateChanged?.Invoke(false);
    }
}

/// <summary>
/// Base class for all dialogs. Wire the close button's onClick to CloseDialog.
/// </summary>
public abstract class DialogBase : MonoBehaviour
{
    public void CloseDialog() => DialogManager.Instance.Pop();
}
```

---

### MVVM Pattern: ViewModel + View (UI Toolkit)

ViewModel is a plain C# class (not a MonoBehaviour); the View is a MonoBehaviour that owns the UIDocument.

```csharp
using System;
using UnityEngine;
using UnityEngine.UIElements;

// ─── ViewModel ───────────────────────────────────────────────────────────────

/// <summary>
/// Holds observable state for the player stats panel.
/// Raises PropertyChanged when any value mutates so the View can update selectively.
/// </summary>
public class PlayerStatsViewModel
{
    public event Action<string, object> PropertyChanged;

    private int _level;
    private float _xp;
    private float _xpToNextLevel;

    public int Level
    {
        get => _level;
        set { _level = value; PropertyChanged?.Invoke(nameof(Level), value); }
    }

    public float XP
    {
        get => _xp;
        set { _xp = value; PropertyChanged?.Invoke(nameof(XP), value); }
    }

    public float XPToNextLevel
    {
        get => _xpToNextLevel;
        set { _xpToNextLevel = value; PropertyChanged?.Invoke(nameof(XPToNextLevel), value); }
    }

    public float XPFraction => _xpToNextLevel > 0f ? Mathf.Clamp01(_xp / _xpToNextLevel) : 0f;
}

// ─── View ────────────────────────────────────────────────────────────────────

/// <summary>
/// Owns the UIDocument and responds to ViewModel property changes.
/// Does not contain any game logic — only reads from the ViewModel and writes to UI.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class PlayerStatsView : MonoBehaviour
{
    private PlayerStatsViewModel _viewModel;

    private Label _levelLabel;
    private VisualElement _xpFill;
    private Label _xpLabel;

    public void Bind(PlayerStatsViewModel viewModel)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnPropertyChanged;

        _viewModel = viewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnPropertyChanged;
            RefreshAll();
        }
    }

    private void Awake()
    {
        UIDocument doc = GetComponent<UIDocument>();
        VisualElement root = doc.rootVisualElement;

        _levelLabel = root.Q<Label>("level-value");
        _xpFill     = root.Q<VisualElement>("xp-fill");
        _xpLabel    = root.Q<Label>("xp-value");
    }

    private void OnDisable()
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnPropertyChanged;
    }

    private void OnPropertyChanged(string propertyName, object _)
    {
        switch (propertyName)
        {
            case nameof(PlayerStatsViewModel.Level):
                _levelLabel.text = _viewModel.Level.ToString();
                break;
            case nameof(PlayerStatsViewModel.XP):
            case nameof(PlayerStatsViewModel.XPToNextLevel):
                UpdateXP();
                break;
        }
    }

    private void RefreshAll()
    {
        _levelLabel.text = _viewModel.Level.ToString();
        UpdateXP();
    }

    private void UpdateXP()
    {
        float fraction = _viewModel.XPFraction;
        _xpFill.style.width = Length.Percent(fraction * 100f);
        _xpLabel.text = $"{_viewModel.XP:0} / {_viewModel.XPToNextLevel:0}";
    }
}
```

## Anti-Examples

### Mixing UI Toolkit and uGUI on the Same Screen

```csharp
// BAD: One screen rendered by both systems — event handling, render order,
// and input routing differ between them. The result is unpredictable z-ordering
// and input conflicts.
public class MainMenuController : MonoBehaviour
{
    [SerializeField] private UIDocument _toolkitBackground; // UI Toolkit
    [SerializeField] private Canvas _ugGuiButtonPanel;      // uGUI overlay

    // These two systems do not share a layout or input pass.
    // Pointer events hitting the uGUI canvas may or may not reach the UIDocument.
}

// GOOD: Commit to one system per screen. If world-space labels are needed
// alongside a screen HUD, the world-space parts use a separate uGUI Canvas
// (World Space render mode) while the screen HUD uses UI Toolkit or its own
// Screen Space Canvas — and the two surfaces never overlap.
```

---

### Rebuilding the Entire HUD Every Frame

```csharp
// BAD: Generates garbage and causes layout recalculations every frame,
// even when health has not changed.
public class BadHealthHUD : MonoBehaviour
{
    [SerializeField] private PlayerHealth _health;
    [SerializeField] private UnityEngine.UI.Text _label;

    private void Update()
    {
        _label.text = $"{_health.Current} / {_health.Max}"; // allocation every frame
    }
}

// GOOD: Update only when the value changes via an event callback (see Code Examples).
```

---

### World-Space Canvas with Pixel Perfect Mode

```csharp
// BAD: Pixel Perfect mode is only valid for Screen Space canvases.
// On a World Space canvas it does nothing useful and can cause visual glitches.
public class BadWorldLabel : MonoBehaviour
{
    private void Awake()
    {
        Canvas canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.pixelPerfect = true; // Has no effect; confuses readers of this code
    }
}

// GOOD: Leave pixelPerfect false (default) on World Space canvases.
```

---

### Hardcoded Screen Coordinates

```csharp
// BAD: Breaks on any resolution other than the development machine.
public class BadHUDPositioner : MonoBehaviour
{
    [SerializeField] private RectTransform _panel;

    private void Start()
    {
        _panel.anchoredPosition = new Vector2(1820f, 980f); // Only correct at 1920×1080
    }
}

// GOOD: Use anchor presets (top-right, bottom-left, etc.) in the Inspector,
// then set anchoredPosition to a small fixed offset from the anchor point
// (e.g., new Vector2(-20f, -20f) for 20px inset from top-right).
// The CanvasScaler handles the scaling; you supply only the design-space offset.
```

## Cross-References

- `hades:component-design` — SO event channels and C# events for pushing data to UI
- `hades:unity-architect` — deciding where UI managers sit in the scene hierarchy
- `hades:data-modeling` — ScriptableObject-based data sources that feed UI ViewModels

**Hades graph tools useful for UI work:**
- `find_components_using_pattern("UIDocument")` — locate existing UI Toolkit screens
- `find_components_using_pattern("Canvas")` — locate existing uGUI canvases
- `search_by_name("*.uxml")` — find UXML templates
- `search_by_name("*.uss")` — find USS stylesheets
- `recall_memory("UI architecture toolkit uGUI")` — retrieve the project's documented UI strategy
- `get_project_summary` — understand the overall project scope before recommending a UI approach
- `propose_memory_update` — record a new UI architecture decision after agreement with the user
