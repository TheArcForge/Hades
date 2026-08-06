---
description: "Use when working with URP rendering — Shader Graph patterns, custom render features, URP-specific post-processing, material setup, and URP shader optimization."
---

# Unity Shaders — URP

Guidance for Universal Render Pipeline rendering work: when to use Shader Graph versus hand-written HLSL, how to author custom Render Features, how to handle per-instance variation without breaking GPU instancing, and how to keep shader complexity within mobile budgets.

## When to Apply

Activate when the conversation involves:
- Creating or editing `.shadergraph` assets targeting URP
- Writing custom HLSL shader passes for URP (Lit, Unlit, Sprite)
- Implementing a `ScriptableRendererFeature` or `ScriptableRenderPass`
- Setting up URP post-processing via Volume overrides or custom Blit passes
- Per-instance material variation with `MaterialPropertyBlock`
- Diagnosing URP-specific shader compilation errors or missing keywords
- Optimizing shader complexity for mobile or low-end hardware
- Migrating Built-in shaders to URP equivalents

Do NOT activate for HDRP questions — redirect to `hades:unity-shaders-hdrp`. Do NOT activate for VFX Graph particle shaders — redirect to `hades:unity-vfx`.

## Project Context Check

Before making recommendations:

1. **Confirm the render pipeline:**
   - Call `analyze_render_pipeline()` — CRITICAL first step. If the result indicates HDRP, stop and redirect to `hades:unity-shaders-hdrp`. If Built-in, warn the user and note the migration path.
   - Confirm the `UniversalRenderPipelineAsset` is assigned in Project Settings > Graphics.

2. **Understand existing shader assets:**
   - Call `search_by_name("*.shadergraph")` to inventory Shader Graph assets already in the project and identify naming conventions.
   - Call `search_by_name("*Render*Feature*")` to find custom `ScriptableRendererFeature` implementations already in use — align naming and patterns.

3. **Check documented decisions:**
   - Call `recall_memory("rendering shader URP")` to surface any team decisions about graph vs. hand-written HLSL, approved shader patterns, or performance budgets.
   - If a recalled decision shows a `warning` validation status, surface the conflict to the user before proceeding.

4. **Adapt recommendations based on findings:**
   - If Shader Graph is in heavy use → prefer graphs for new effects, hand-written only when the graph cannot express the required logic.
   - If a mobile performance budget is documented → enforce vertex/instruction count limits.
   - After the user agrees on a new pattern not yet documented, call `propose_memory_update` to record it.

## Decision Framework

### Shader Graph vs Hand-Written HLSL

#### Choose Shader Graph when:
- The effect is visual (dissolve, rim light, color grade, triplanar blend) with no algorithmic complexity that requires explicit loop control.
- Non-engineers need to iterate on the look — graphs are readable and tweak-friendly.
- The effect is a variation on a standard URP Lit or Unlit surface — graph handles the PBR wiring automatically.
- Rapid prototyping is the priority.

#### Choose hand-written HLSL when:
- The pass is procedural or compute-heavy (custom depth-normals pass, screen-space effects, custom shadow casters).
- You need full control over vertex attributes or a custom vertex interpolator.
- The graph would require many Custom Function nodes that effectively become embedded HLSL anyway.
- The shader must be as small as possible for mobile — graphs sometimes generate redundant variants.

#### Common shader patterns and their recommended approach:

| Effect | Approach |
|--------|----------|
| Dissolve (noise clip) | Shader Graph — Sample Texture 2D → Step |
| Outline (post-process) | Custom Render Feature + Blit HLSL pass |
| Triplanar texturing | Shader Graph with Custom Function or Subgraph |
| Toon/cel shading | Custom Lit Shader Graph + custom lighting function |
| Water (waves + refraction) | Shader Graph — vertex offset + GrabPass alternative via opaque texture |
| Fullscreen color grade | Renderer Feature with Blit + custom HLSL |
| Decals | URP Decal Projector (built-in) — no custom shader needed |

### Custom Render Features: When and How

Use a `ScriptableRendererFeature` when:
- You need an injected draw call at a specific point in the frame (BeforeRenderingOpaques, AfterRenderingTransparents, etc.).
- You want a fullscreen Blit effect that runs every frame (fog, outline, scan lines).
- You need to read camera normals or depth in screen-space.

Prefer **Volume overrides** when:
- The effect already exists as a URP Volume component (Bloom, SSAO, Color Adjustments, etc.).
- You only need to blend parameters per-camera or per-region — no extra draw calls needed.

Renderer Features vs Volume overrides:
- Volume overrides → tweak built-in post-processing at runtime, blendable, zero code for common effects.
- Renderer Features → arbitrary injected passes, full control, higher implementation cost.

### Per-Instance Variation

Never create a unique `Material` per object for color variation. Unique materials break GPU instancing and generate one draw call per object.

Use `MaterialPropertyBlock` instead:
- Override per-instance properties without creating a new material asset.
- Works with GPU instancing enabled on the material.
- Costs one `SetPropertyBlock` call per object on the CPU — cheap.

### LOD and Shader Complexity for Mobile

- Use the `#pragma multi_compile` sparingly — every variant doubles compile time and memory.
- Prefer `#pragma shader_feature` for features toggled at material authoring time (not runtime).
- Limit ALU instructions: aim for ≤ 60 instructions on mobile Unlit, ≤ 120 on Lit.
- Use `half` precision for color and normal work on mobile; `float` only for position and UV.
- Disable Depth Priming on Renderer if the scene has many transparent objects — it adds an extra depth prepass cost.

## Code Examples

### Custom URP Render Feature (Full Implementation)

A `ScriptableRendererFeature` that injects a fullscreen Blit pass. This is the canonical skeleton for any screen-space effect.

```csharp
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Renderer Feature that injects a fullscreen Blit using a custom material.
/// Add via the URP Renderer asset > Add Renderer Feature > FullscreenBlitFeature.
/// Assign the material in the feature inspector.
/// </summary>
public class FullscreenBlitFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public Material blitMaterial;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    [SerializeField] private Settings _settings = new();

    private FullscreenBlitPass _pass;

    public override void Create()
    {
        _pass = new FullscreenBlitPass(_settings.blitMaterial, _settings.renderPassEvent);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Skip in Scene view and preview cameras to avoid editor performance overhead.
        if (renderingData.cameraData.cameraType == CameraType.Preview) return;
        if (_settings.blitMaterial == null) return;

        _pass.Setup(renderer.cameraColorTargetHandle);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }
}

/// <summary>
/// The injected render pass. Blits the camera color through the assigned material.
/// </summary>
public class FullscreenBlitPass : ScriptableRenderPass, System.IDisposable
{
    private readonly Material _material;
    private RTHandle _tempRt;
    private RTHandle _cameraColorHandle;

    private static readonly int TempTexId = Shader.PropertyToID("_TempTex");

    public FullscreenBlitPass(Material material, RenderPassEvent renderPassEvent)
    {
        _material = material;
        this.renderPassEvent = renderPassEvent;
    }

    public void Setup(RTHandle cameraColorHandle)
    {
        _cameraColorHandle = cameraColorHandle;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        // Allocate a temp RT matching the camera descriptor.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        RenderingUtils.ReAllocateIfNeeded(ref _tempRt, desc, name: "_TempBlitTex");
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (_material == null) return;

        CommandBuffer cmd = CommandBufferPool.Get("FullscreenBlit");

        // Blit camera → temp, apply material effect, blit temp → camera.
        Blitter.BlitCameraTexture(cmd, _cameraColorHandle, _tempRt, _material, 0);
        Blitter.BlitCameraTexture(cmd, _tempRt, _cameraColorHandle);

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public override void OnCameraCleanup(CommandBuffer cmd) { }

    public void Dispose()
    {
        _tempRt?.Release();
    }
}
```

---

### MaterialPropertyBlock for Per-Instance Color (Full Implementation)

Vary color (or any material property) per GameObject without creating unique material instances, preserving GPU instancing.

```csharp
using UnityEngine;

/// <summary>
/// Sets a per-instance color on a Renderer using MaterialPropertyBlock.
/// Attach to any GameObject with a MeshRenderer or SkinnedMeshRenderer.
/// The material must have GPU Instancing enabled and expose a _Color property.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class InstanceColor : MonoBehaviour
{
    [SerializeField] private Color _color = Color.white;

    private Renderer _renderer;
    private MaterialPropertyBlock _block;

    // Cache the property ID once — faster than string lookup every frame.
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _block    = new MaterialPropertyBlock();
    }

    private void OnEnable() => Apply();

    /// <summary>Call at runtime to change the instance color without a new material.</summary>
    public void SetColor(Color color)
    {
        _color = color;
        Apply();
    }

    private void Apply()
    {
        // Read current block state to preserve other per-instance properties.
        _renderer.GetPropertyBlock(_block);
        _block.SetColor(ColorId, _color);
        _renderer.SetPropertyBlock(_block);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Reflect inspector changes in the Scene view without entering Play mode.
        if (_renderer == null) _renderer = GetComponent<Renderer>();
        if (_block    == null) _block    = new MaterialPropertyBlock();
        Apply();
    }
#endif
}
```

---

### Fullscreen Shader Effect via Blit Render Feature (HLSL)

The HLSL shader used with the `FullscreenBlitFeature` above. This implements a simple scanline effect but demonstrates the correct URP fullscreen shader structure.

```hlsl
Shader "Custom/URP/FullscreenScanline"
{
    Properties
    {
        // _MainTex is set automatically by Blitter.BlitCameraTexture — do not expose in inspector.
        _ScanlineFrequency ("Scanline Frequency", Float) = 200.0
        _ScanlineIntensity  ("Scanline Intensity",  Range(0,1)) = 0.15
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "FullscreenScanline"

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _ScanlineFrequency;
            float _ScanlineIntensity;

            half4 Frag(Varyings input) : SV_Target
            {
                // Sample the camera color via the Blit include's helper.
                half4 color = FragBlit(input, sampler_LinearClamp);

                // Derive screen-space Y from clip position (0..1).
                float scanline = sin(input.positionCS.y * _ScanlineFrequency) * 0.5 + 0.5;
                color.rgb *= lerp(1.0, scanline, _ScanlineIntensity);

                return color;
            }
            ENDHLSL
        }
    }
}
```

---

### Simple Custom URP Shader (Hand-Written HLSL, Full Pass)

A minimal hand-written URP Unlit shader with texture support and a custom tint — demonstrates the required URP includes and CBUFFER layout.

```hlsl
Shader "Custom/URP/SimpleUnlit"
{
    Properties
    {
        _BaseMap   ("Texture",   2D)    = "white" {}
        _BaseColor ("Tint",      Color) = (1,1,1,1)
        _Cutoff    ("Alpha Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType"  = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"       = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            // Required URP keyword for fog, instancing, etc.
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // All material properties must live in CBUFFER for SRP batching.
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float  fogFactor   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv          = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor   = ComputeFogFactor(output.positionHCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color    = texColor * _BaseColor;

                // Alpha clip for cutout materials.
                clip(color.a - _Cutoff);

                // Apply URP fog.
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
            }
            ENDHLSL
        }

        // ShadowCaster pass — required for the object to cast shadows in URP.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half   _Cutoff;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings ShadowVert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                // Bias the shadow position to avoid self-shadowing artefacts.
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.positionHCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _MainLightPosition.xyz));
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
```

## Anti-Examples

### Built-In Shader Syntax in URP

```hlsl
// BAD — Built-in pipeline includes and semantics do not exist in URP.
// These produce missing-include or missing-macro compile errors.
#include "UnityCG.cginc"          // Built-in only
#include "Lighting.cginc"         // Built-in only

float4 _Color;                    // Not in a CBUFFER — breaks SRP Batcher

v2f vert(appdata v)               // appdata struct is Built-in only
{
    v2f o;
    o.vertex = UnityObjectToClipPos(v.vertex);  // Built-in macro
    return o;
}

// GOOD — Use URP includes and macros
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    half4 _BaseColor;
CBUFFER_END

Varyings Vert(Attributes input)
{
    Varyings output;
    output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}
```

---

### Unique Material Per Object for Color Variation

```csharp
// BAD — Instantiates a new material for each object at runtime.
// Breaks GPU instancing and SRP Batching. With 500 objects = 500 draw calls.
void Start()
{
    GetComponent<Renderer>().material.color = Random.ColorHSV();
}

// Also bad — renderer.material (without assignment) creates an instance implicitly.
void Update()
{
    GetComponent<Renderer>().material.SetFloat("_Dissolve", dissolveAmount);
}

// GOOD — Use MaterialPropertyBlock (see Code Examples above).
// Preserves instancing, zero material allocations.
private MaterialPropertyBlock _block;
private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

void Awake() => _block = new MaterialPropertyBlock();

void Update()
{
    _renderer.GetPropertyBlock(_block);
    _block.SetFloat(DissolveId, dissolveAmount);
    _renderer.SetPropertyBlock(_block);
}
```

---

### Complex Pixel Shader on Mobile Without LOD

```hlsl
// BAD — High ALU count, three texture samples, and a loop in the fragment shader.
// Runs at 15fps on mid-range mobile.
half4 Frag(Varyings input) : SV_Target
{
    half4 color = half4(0,0,0,0);
    for (int i = 0; i < 8; i++)           // loops in frag = expensive
    {
        color += SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, input.uv + i * 0.01);
    }
    color *= SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
    color += SAMPLE_TEXTURE2D(_EmissiveMap, sampler_EmissiveMap, input.uv);
    return color;
}

// GOOD — Provide a simplified LOD variant. In URP, use shader_feature to compile
// a low-quality path and toggle it per-material or per-platform.
#pragma shader_feature_local _ HIGH_QUALITY_SHADER
```

---

### OnRenderImage in URP

```csharp
// BAD — OnRenderImage is a Built-in pipeline method. It does not exist in URP.
// The callback is never called and silently does nothing.
private void OnRenderImage(RenderTexture src, RenderTexture dest)
{
    Graphics.Blit(src, dest, _effectMaterial);
}

// GOOD — Implement a ScriptableRendererFeature with a Blit pass (see Code Examples).
```

## Cross-References

- Related skills: `hades:unity-performance`, `hades:unity-vfx`
- Hades MCP tools used here: `analyze_render_pipeline`, `search_by_name`, `recall_memory`, `propose_memory_update`
- If pipeline is HDRP → `hades:unity-shaders-hdrp`
- Unity docs: [URP Shader documentation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/writing-shaders-urp-basic-introduction.html), [ScriptableRendererFeature API](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/api/UnityEngine.Rendering.Universal.ScriptableRendererFeature.html), [MaterialPropertyBlock](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/MaterialPropertyBlock.html)
