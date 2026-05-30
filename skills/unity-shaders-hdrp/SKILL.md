---
description: "Use when working with HDRP rendering — HDRP-specific material setup, custom passes, volume overrides, ray tracing considerations, and HDRP shader patterns."
---

# Unity Shaders — HDRP

Guidance for High Definition Render Pipeline rendering work: HDRP material types and when to use them, the Custom Pass framework, the Volume system for per-area blending, ray tracing hardware requirements, and safe patterns for HDRP Shader Graph.

## When to Apply

Activate when the conversation involves:
- Setting up HDRP Lit, Unlit, StackLit, Fabric, Hair, or Eye materials
- Creating or editing `.shadergraph` assets targeting HDRP
- Writing a `CustomPass` C# class (fullscreen or object pass)
- Configuring Volume profiles for different areas of the scene
- Evaluating or enabling ray tracing features (reflections, shadows, AO)
- Diagnosing HDRP-specific compile errors or missing shader features
- Optimizing HDRP scenes for PC/console performance targets
- Using HDRP's decal system or the Decal Projector component

Do NOT activate for URP questions — redirect to `hades:unity-shaders-urp`. Do NOT activate for VFX Graph particle shaders — redirect to `hades:unity-vfx`.

## Project Context Check

Before making recommendations:

1. **Confirm the render pipeline:**
   - Call `analyze_render_pipeline()` — CRITICAL first step. If the result indicates URP, stop and redirect to `hades:unity-shaders-urp`. If Built-in, warn the user and note the migration path.
   - Confirm the `HDRenderPipelineAsset` is assigned in Project Settings > Graphics.

2. **Understand existing shader assets:**
   - Call `search_by_name("*.shadergraph")` to inventory Shader Graph assets already in the project and identify naming conventions and material types in use.
   - Call `search_by_name("*CustomPass*")` to find existing Custom Pass implementations and understand injection points already used.

3. **Check documented decisions:**
   - Call `recall_memory("rendering shader HDRP")` to surface any team decisions about material types, approved ray tracing features, or performance targets.
   - If a recalled decision shows a `warning` validation status, surface the conflict to the user before proceeding.

4. **Adapt recommendations based on findings:**
   - If StackLit is already used on hero assets only → recommend Lit for new secondary characters.
   - If ray tracing is disabled in the project HDRP asset → do not recommend RT features without first checking hardware support.
   - After the user agrees on a pattern not yet documented, call `propose_memory_update` to record it.

## Decision Framework

### HDRP Material Types

HDRP exposes several Shader Graph master stacks / material types. Choose deliberately:

| Material Type | When to Use | Avoid When |
|---------------|-------------|------------|
| **Lit** | Characters, props, environments — general-purpose PBR with metallic/smoothness workflow | Almost never; it's the default correct choice |
| **Unlit** | Emissive panels, UI elements in world space, VFX billboards | Object should respond to scene lighting |
| **StackLit** | Hero materials needing dual specular lobes, anisotropy, coat — complex surfaces like car paint, brushed metal | Secondary/background objects; too expensive for crowds or large meshes |
| **Fabric** | Cloth with thread highlight (cotton, silk) | Non-fabric surfaces; only for textile-specific scattering |
| **Hair** | Kajiya-Kay strand highlight, depth-sorted hair geometry | Skin, cloth, props |
| **Eye** | Cornea refraction + limbal ring for realistic eyes | Stylized eyes; use Lit instead if eye detail is low |

#### Rule of thumb:
- Use **Lit** by default.
- Upgrade to **StackLit/Fabric/Hair/Eye** only for hero characters where the specialized shading is perceptible at game camera distance.
- **Never** use StackLit on mobile builds — HDRP itself is desktop/console only, but even within HDRP platforms StackLit doubles ALU cost.

### Custom Pass Framework vs Full Screen Custom Pass

#### Use `CustomPass` (object-based) when:
- You need to render a subset of objects with a replacement shader (e.g., outline object highlight, X-ray vision).
- The effect must be attached to the scene and composited into the frame at a specific injection point.

#### Use `FullScreenCustomPass` when:
- The effect is purely screen-space (fog layer, scanline overlay, color dodge).
- You want a fullscreen Blit with a custom material without managing object filtering.

Injection points (in render order):
```
BeforeRendering           — Before any rendering (custom pre-pass depth)
BeforePreRefraction       — Before transparent objects that need refraction
BeforeTransparent         — Before all transparent objects
BeforePostProcess         — After opaque and transparent, before post-process
AfterPostProcess          — Final composite step, after TAA/upscaling
```

### Volume System — Local vs Global, Profiles, Overrides

- **Global Volume** with priority 0: project-wide baseline settings (Exposure, Fog, SSAO defaults).
- **Local Volumes** (Box or Sphere Collider): per-area overrides that blend as the camera enters. Use for indoor/outdoor exposure transitions, fog density changes, and water caustics areas.
- One **Volume Profile** per distinct visual zone; do not add overrides to the global profile unless they apply everywhere.
- Set `Weight` and `Blend Distance` on local volumes — instant snapping (zero blend) is almost always wrong.

HDRP Volume components that are especially high-value:
- `Exposure` (automatic or manual) — always override in local volumes for indoor vs outdoor.
- `Fog` — HDRP Volumetric fog is expensive; reduce Slice Count in the HDRP asset for lower-end targets.
- `ScreenSpaceAmbientOcclusion` — disable or reduce radius in performance-sensitive scenes.
- `ContactShadows` — high quality but add a full-scene shadow ray; keep enabled only where impactful.

### Ray Tracing Considerations

HDRP ray tracing features require DXR-capable GPU (NVIDIA Turing / AMD RDNA2 or later) and Windows 10 with DX12.

**Before enabling any RT feature:**
- Check `SystemInfo.supportsRayTracing` at runtime and fall back to rasterized equivalent.
- Ray tracing features add GPU overhead per active feature. Enable only what is perceptible at shipping quality.
- Never enable RT features on HDRP assets targeting console without verifying platform support.

| RT Feature | Cost | Fallback |
|------------|------|---------|
| Screen Space Reflections (RT) | Medium | HDRP SSR (screen-space, raster) |
| Ray Traced Shadows | High | Shadow maps |
| Ray Traced AO | Medium | SSAO |
| Path Tracing | Extreme — offline/cutscene only | Lit + SSAO + SSR |

### HDRP Shader Graph Features

HDRP Shader Graph adds context inputs unavailable in URP:
- `Exposure` node — physically correct exposure for emissive.
- `HD Scene Color` — opaque scene color for refraction without GrabPass.
- `Diffusion Profile` — subsurface scattering profile reference.
- `Baked GI` — custom indirect lighting contribution.

Always use the HDRP-specific `Sample Buffer` and `Normal From Texture` nodes rather than their generic equivalents — the generic ones may sample the wrong buffer in HDRP.

## Code Examples

### Custom Pass Volume — C# Class + Volume Setup (Full Implementation)

A Custom Pass that renders selected objects with a replacement material (e.g., outline highlight or X-ray).

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Custom Pass that re-renders objects on a specific layer using a replacement material.
/// Use cases: outline highlight, X-ray vision, stealth silhouette.
///
/// Setup:
///   1. Create a Custom Pass Volume in the scene (Add Component > Volume > Custom Pass Volume).
///   2. Add this pass via the + button in the Custom Pass Volume inspector.
///   3. Set TargetLayer to the layer used by objects that should receive the effect.
///   4. Assign the replacement material (e.g., an Unlit bright-color shader).
/// </summary>
public class ObjectReplacementPass : CustomPass
{
    [Tooltip("Layer mask for objects to re-render with the replacement material.")]
    public LayerMask targetLayer = 0;

    [Tooltip("Material used to replace the object's surface shader during this pass.")]
    public Material replacementMaterial;

    [Tooltip("Where in the frame this pass runs.")]
    public CustomPassInjectionPoint injectionPoint = CustomPassInjectionPoint.BeforePostProcess;

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) { }

    protected override void Execute(CustomPassContext ctx)
    {
        if (replacementMaterial == null) return;

        // Build a ShaderTagId list matching the HDRP forward passes we want to replace.
        var shaderTags = new ShaderTagId[]
        {
            new ShaderTagId("Forward"),
            new ShaderTagId("ForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
        };

        // Sort front-to-back for opaque objects; back-to-front if replacement is transparent.
        var sortFlags = SortingCriteria.CommonOpaque;
        var rendererListDesc = new RendererListDesc(shaderTags, ctx.cullingResults, ctx.hdCamera.camera)
        {
            layerMask               = targetLayer,
            renderQueueRange        = RenderQueueRange.all,
            sortingCriteria         = sortFlags,
            overrideMaterial        = replacementMaterial,
            overrideMaterialPassIndex = 0,
        };

        CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
        HDUtils.DrawRendererList(ctx.renderContext, ctx.cmd, RendererList.Create(rendererListDesc));
    }

    protected override void Cleanup() { }
}
```

---

### HDRP Volume Profile — Per-Area Setup Script (Full Implementation)

Programmatically configures a Volume Profile for an indoor area: darker exposure, no volumetric fog, stronger AO.

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Configures a VolumeProfile at runtime for an indoor zone.
/// Attach to the GameObject that holds the Volume component for the indoor area.
/// Call ApplyIndoorSettings() when the player enters, ApplyOutdoorSettings() when they leave.
/// </summary>
[RequireComponent(typeof(Volume))]
public class IndoorVolumeController : MonoBehaviour
{
    [Header("Indoor Settings")]
    [SerializeField] private float _indoorExposureEV = -1.5f;
    [SerializeField] private float _indoorFogMeanFreePath = 50f;
    [SerializeField] private float _indoorAOIntensity = 1.8f;

    [Header("Outdoor Settings")]
    [SerializeField] private float _outdoorExposureEV = 0f;
    [SerializeField] private float _outdoorFogMeanFreePath = 400f;
    [SerializeField] private float _outdoorAOIntensity = 0.8f;

    private Volume _volume;
    private Exposure _exposure;
    private Fog _fog;
    private ScreenSpaceAmbientOcclusion _ssao;

    private void Awake()
    {
        _volume = GetComponent<Volume>();

        // TryGet returns false if the component isn't in the profile — safe to skip.
        _volume.profile.TryGet(out _exposure);
        _volume.profile.TryGet(out _fog);
        _volume.profile.TryGet(out _ssao);
    }

    public void ApplyIndoorSettings()
    {
        if (_exposure != null)
        {
            _exposure.mode.Override(ExposureMode.Fixed);
            _exposure.fixedExposure.Override(_indoorExposureEV);
        }

        if (_fog != null)
        {
            _fog.enabled.Override(true);
            _fog.meanFreePath.Override(_indoorFogMeanFreePath);
        }

        if (_ssao != null)
        {
            _ssao.intensity.Override(_indoorAOIntensity);
        }
    }

    public void ApplyOutdoorSettings()
    {
        if (_exposure != null)
        {
            _exposure.mode.Override(ExposureMode.Automatic);
            _exposure.fixedExposure.Override(_outdoorExposureEV);
        }

        if (_fog != null)
        {
            _fog.meanFreePath.Override(_outdoorFogMeanFreePath);
        }

        if (_ssao != null)
        {
            _ssao.intensity.Override(_outdoorAOIntensity);
        }
    }
}
```

---

### HDRP Lit Material Setup Script (Full Implementation)

Programmatically sets HDRP Lit material properties for metallic surfaces — useful for runtime material swaps or procedural asset setup.

```csharp
using UnityEngine;

/// <summary>
/// Applies HDRP Lit material properties for a metallic surface.
/// Use for runtime material configuration, procedural asset pipelines,
/// or one-shot editor tooling (wrap with [ContextMenu] for editor use).
///
/// Property names are fixed for HDRP Lit. Confirm via the shader source at:
/// Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.shader
/// </summary>
[RequireComponent(typeof(Renderer))]
public class HDRPLitMaterialSetup : MonoBehaviour
{
    [Header("Surface")]
    [SerializeField] private Texture2D _baseColorMap;
    [SerializeField] private Color     _baseColor    = Color.white;
    [SerializeField, Range(0f, 1f)] private float _metallic   = 0f;
    [SerializeField, Range(0f, 1f)] private float _smoothness = 0.5f;

    [Header("Normal / Detail")]
    [SerializeField] private Texture2D _normalMap;
    [SerializeField, Range(0f, 2f)] private float _normalScale = 1f;

    [Header("Emissive")]
    [SerializeField] private Color _emissiveColor = Color.black;
    [SerializeField] private float _emissiveIntensity = 0f; // in nits

    // HDRP Lit property IDs — cache to avoid per-frame string allocations.
    private static readonly int BaseColorMapId    = Shader.PropertyToID("_BaseColorMap");
    private static readonly int BaseColorId       = Shader.PropertyToID("_BaseColor");
    private static readonly int MetallicId        = Shader.PropertyToID("_Metallic");
    private static readonly int SmoothnessId      = Shader.PropertyToID("_Smoothness");
    private static readonly int NormalMapId       = Shader.PropertyToID("_NormalMap");
    private static readonly int NormalScaleId     = Shader.PropertyToID("_NormalScale");
    private static readonly int EmissiveColorId   = Shader.PropertyToID("_EmissiveColor");

    private Renderer _renderer;
    private MaterialPropertyBlock _block;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _block    = new MaterialPropertyBlock();
    }

    private void Start() => Apply();

    public void Apply()
    {
        _renderer.GetPropertyBlock(_block);

        if (_baseColorMap != null) _block.SetTexture(BaseColorMapId, _baseColorMap);
        _block.SetColor(BaseColorId, _baseColor);
        _block.SetFloat(MetallicId,   _metallic);
        _block.SetFloat(SmoothnessId, _smoothness);

        if (_normalMap != null)
        {
            _block.SetTexture(NormalMapId, _normalMap);
            _block.SetFloat(NormalScaleId, _normalScale);
        }

        // Emissive color in HDRP is stored pre-multiplied by intensity (in nits).
        _block.SetColor(EmissiveColorId, _emissiveColor * _emissiveIntensity);

        _renderer.SetPropertyBlock(_block);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (_renderer == null) _renderer = GetComponent<Renderer>();
        if (_block    == null) _block    = new MaterialPropertyBlock();
        Apply();
    }
#endif
}
```

---

### Fullscreen Custom Pass — Outline / Edge Detection (Full Implementation)

A `FullScreenCustomPass` that reads the HDRP normal buffer and writes a screen-space outline. Demonstrates the correct buffer access pattern for HDRP fullscreen passes.

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Fullscreen Custom Pass that draws a screen-space edge outline by sampling
/// the camera normal buffer. Runs AfterPostProcess so it appears on top of TAA.
///
/// Setup:
///   1. Create a Custom Pass Volume in the scene.
///   2. Add this pass via the + button.
///   3. Set InjectionPoint to AfterPostProcess in the inspector.
///   4. Assign the outline material (shader: Custom/HDRP/FullscreenOutline).
/// </summary>
public class FullscreenOutlinePass : CustomPass
{
    [Tooltip("Material using Custom/HDRP/FullscreenOutline shader.")]
    public Material outlineMaterial;

    [Tooltip("Outline edge threshold (0.05–0.3 works well for most content).")]
    [Range(0.01f, 1f)] public float edgeThreshold = 0.1f;

    [Tooltip("Color of the outline drawn over the frame.")]
    public Color outlineColor = Color.black;

    private static readonly int EdgeThresholdId = Shader.PropertyToID("_EdgeThreshold");
    private static readonly int OutlineColorId  = Shader.PropertyToID("_OutlineColor");

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) { }

    protected override void Execute(CustomPassContext ctx)
    {
        if (outlineMaterial == null) return;

        // Pass parameters to the shader via MaterialPropertyBlock equivalent for custom passes.
        outlineMaterial.SetFloat(EdgeThresholdId, edgeThreshold);
        outlineMaterial.SetColor(OutlineColorId,  outlineColor);

        // Blit to the camera color buffer; HDRP provides the normal buffer automatically
        // when the shader samples _NormalBufferTexture.
        HDUtils.DrawFullScreen(ctx.cmd, outlineMaterial, ctx.cameraColorBuffer, shaderPassId: 0);
    }

    protected override void Cleanup() { }
}
```

The companion HLSL shader for the pass above:

```hlsl
Shader "Custom/HDRP/FullscreenOutline"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "FullscreenOutline"
            ZWrite Off ZTest Always Cull Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

            float  _EdgeThreshold;
            float4 _OutlineColor;

            half4 Frag(Varyings varyings) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);

                float depth;
                NormalData normalData;

                // Decode HDRP normal buffer at the current pixel and its 4 neighbours.
                float2 uv = varyings.positionCS.xy;

                DecodeFromNormalBuffer(uv,                 normalData);
                float3 n  = normalData.normalWS;

                DecodeFromNormalBuffer(uv + float2( 1, 0), normalData); float3 nR = normalData.normalWS;
                DecodeFromNormalBuffer(uv + float2(-1, 0), normalData); float3 nL = normalData.normalWS;
                DecodeFromNormalBuffer(uv + float2( 0, 1), normalData); float3 nU = normalData.normalWS;
                DecodeFromNormalBuffer(uv + float2( 0,-1), normalData); float3 nD = normalData.normalWS;

                float edge = saturate(
                    length(n - nR) + length(n - nL) +
                    length(n - nU) + length(n - nD)
                );

                edge = step(_EdgeThreshold, edge);

                // Sample existing camera color; blend outline over it.
                float4 color = LoadCameraColor(varyings.positionCS.xy, 0);
                return lerp(color, _OutlineColor, edge * _OutlineColor.a);
            }
            ENDHLSL
        }
    }
}
```

## Anti-Examples

### Ray Tracing Without Hardware Check

```csharp
// BAD — Unconditionally enabling ray tracing will crash or silently fail
// on machines without DXR support (most mid-range laptops and all consoles).
void Start()
{
    var rtSettings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
    rtSettings.tracing.Override(RayCastingMode.RayTracing); // hard crash on DXR-unsupported GPU
}

// GOOD — Always gate behind a hardware check.
void Start()
{
    bool rtSupported = SystemInfo.supportsRayTracing;
    Debug.Log($"Ray tracing supported: {rtSupported}");

    var ssr = GetComponent<Volume>().profile.Add<ScreenSpaceReflection>(overridesOnly: true);
    ssr.tracing.Override(rtSupported ? RayCastingMode.RayTracing : RayCastingMode.RayMarching);
}
```

---

### Too Many Volume Overrides in One Profile

```csharp
// BAD — A single Volume profile with 20 overrides applied globally.
// Every camera in every scene inherits all settings; subtle global side-effects
// appear in unexpected places and override local volumes unintentionally.
//
// Global volume profile containing: Exposure, Fog, SSAO, Bloom, DOF, ColorGrading,
// ContactShadows, VolumetricClouds, PlanarReflections, MotionBlur, ... (17 more)

// GOOD — Minimal global profile with scene-wide non-negotiables only.
// Push area-specific overrides to local volumes with a defined blend distance.
//   GlobalProfile:    Exposure (Automatic), Fog (baseline), SSAO (default intensity)
//   IndoorProfile:    Exposure (Fixed -1.5 EV), Fog (dense), ContactShadows (on)
//   NightProfile:     Exposure (Fixed -3 EV), Bloom (high threshold), Stars (custom pass)
```

---

### StackLit for Background or Crowd Objects

```csharp
// BAD — StackLit for a background rock or a crowd NPC.
// StackLit evaluates two specular lobes + coat + anisotropy. Even if those
// features are unused, the variant is compiled and the GPU cost is paid.
// On a scene with 200 crowd NPCs this can cost 4ms+ on mid-range GPU.

// GOOD — StackLit only for hero assets where the dual-lobe highlight is visible
// at the intended gameplay camera distance.
//   Hero car (player vehicle, close-up, direct light) → StackLit (car paint dual-lobe)
//   Background vehicles                                 → Lit (metallic + smoothness)
//   Crowd characters                                    → Lit, baked GI contribution only
```

---

### Ignoring LOD for High-Poly HDRP Scenes

```csharp
// BAD — 200k-triangle hero mesh with StackLit used at all distances.
// HDRP does not automatically reduce shader complexity with LOD.
// The GPU renders full StackLit cost for every pixel of every LOD level
// unless you assign a cheaper material to lower LODs.

// GOOD — Assign explicit materials per LOD level.
[RequireComponent(typeof(LODGroup))]
public class LODMaterialSwapper : MonoBehaviour
{
    [SerializeField] private Material[] _materialsPerLOD; // StackLit, Lit, Lit-simple

    private void Start()
    {
        LODGroup group = GetComponent<LODGroup>();
        LOD[] lods = group.GetLODs();

        for (int i = 0; i < lods.Length && i < _materialsPerLOD.Length; i++)
        {
            if (_materialsPerLOD[i] == null) continue;
            foreach (Renderer r in lods[i].renderers)
                r.sharedMaterial = _materialsPerLOD[i];
        }

        group.SetLODs(lods);
        group.RecalculateBounds();
    }
}
```

## Cross-References

- Related skills: `hades:unity-performance`, `hades:unity-vfx`
- Hades MCP tools used here: `analyze_render_pipeline`, `search_by_name`, `recall_memory`, `propose_memory_update`
- If pipeline is URP → `hades:unity-shaders-urp`
- Unity docs: [HDRP Material reference](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Material-Types.html), [Custom Pass framework](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Custom-Pass.html), [Volume system](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Volumes.html), [Ray tracing overview](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/Ray-Tracing-Getting-Started.html)
