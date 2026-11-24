# Reading Fuluma's setup from Unity YAML

Unity text assets are YAML documents separated by `--- !u!<classId> &<fileID>` headers. A reference is
`{fileID: <id>, guid: <guid>, type: <n>}`: no `guid` means "an object in this same file with that fileID";
`fileID: 0` means "none". To find the asset a GUID refers to, search the project's `.meta` files for `guid: <guid>`.

## Script GUIDs (MonoBehaviours)

A component or asset is identified by its `m_Script` GUID, and newer Unity versions also write
`m_EditorClassIdentifier: Saesentsessis.Fuluma.Runtime::<namespace>.<type>`.

| Type | `m_Script` guid |
|---|---|
| `FulumaRenderFeature` | `a950492ccd1347ca9277bdd87e5a99cd` |
| `FakeSpotLight` | `efcbc53ee21f44e895f3ca7e1d02d2d4` |
| `LinkedFakeLight` | `159c5fe021417c741a8246f76e850a3c` |
| `FakeLightClipBox` | `6753e257bb244d948a85c012286378ae` |
| `LightStore` (asset) | `bc472fd110504b0389f7976690e634ec` |

If a GUID doesn't match (a different package version), find it in the package's `Runtime/**/*.cs.meta` files.

## Which renderer draws

- **Quality levels:** `ProjectSettings/QualitySettings.asset` lists `m_QualitySettings`, each with `name:` and
  `customRenderPipeline:` (a URP asset reference; `fileID: 0` = use the default). `m_CurrentQuality` is the Editor's
  active level (0-based). Players pick their level per platform (`m_PerPlatformDefaultQuality`).
- **Default pipeline:** `ProjectSettings/GraphicsSettings.asset` → `m_CustomRenderPipeline`, used by levels without
  their own.
- **URP asset:** `m_RendererDataList` (renderer references) and `m_DefaultRendererIndex`.
- **Camera:** its `UniversalAdditionalCameraData` component (same GameObject) has `m_RendererIndex`; `-1` = the URP
  asset's default renderer. The camera's own `m_CullingMask` (`m_Bits`) is what Camera Layers is tested against.
- **Renderer asset** (`UniversalRendererData`): `m_RendererFeatures` lists the feature sub-assets (in the same file);
  `m_RenderingMode` 0 Forward, 1 Deferred, 2 Forward+, 3 Deferred+. Each feature is its own document further down.
- **Compatibility Mode:** on Unity versions that still have it, `m_EnableRenderCompatibilityMode: 1` in the URP global
  settings asset means render graph is off and Fuluma draws nothing.

## FulumaRenderFeature fields

```yaml
  m_Active: 1                       # 0 = feature disabled
  event: 400                        # RenderPassEvent, see below
  overrideStore: {fileID: 0}        # 0 = draws the default store
  cameraLayers: { m_Bits: 4294967295 }
  lightIntensity: 1
  scatteringPhase: 0                # 0 Isotropic, 1 Henyey-Greenstein
  scatteringAnisotropy: 0.7
  distanceFalloff: 0                # 0 Range Scaled, 1 Unity
  lampGlare: 0                      # 0 None, 1 Point
  fidelityFeatures: 65543           # flags, see below
  nearPlaneFadeDistance: 0.5
  dynamicBufferCount: 3
  spotLightDescriptor: {fileID: 0}  # 0 = built-in proxy LODs
  maxSpotLights: 65536
  spotLightBufferSize: 256
  spotLightShader: {...}            # must not be fileID 0
  clipBoxPieceLimit: 3
  clipBoxBufferSize: 16
  lightBufferScale: 0.5             # 1 = full resolution
  lightBufferLights: 0              # 0 All, 1 Large Only
  largeLightScreenSize: 0.15
  lightBufferUpsample: 0            # 0 Depth Aware, 1 Bilinear
  lightBufferTiming: 0              # 0 Auto, 1 After Opaques, 2 Before Opaques
  lightBufferShader: {...}          # empty = full resolution only
```

**`fidelityFeatures` flags** (sum of): Scene Depth 1, Clip Boxes 2, Near Plane Fade 4, Spot Light LODs 65536, Wide
Cone Precision 131072. Default 65543 = all but Wide Cone Precision. Example: 65541 = Clip Boxes off.

**`event` values** (URP `RenderPassEvent`): 200 After Rendering Pre Passes, 250 Before Rendering Opaques, 300 After
Rendering Opaques, 350 Before Rendering Skybox, 400 After Rendering Skybox (default), 450 Before Rendering
Transparents, 500 After Rendering Transparents, 550/600 Before/After Rendering Post Processing.

## Components

```yaml
# FakeSpotLight
  m_Enabled: 1
  overrideStore: {fileID: 0}        # 0 = default store
  syncTransform: 0                  # 0 static (moves on ApplyTransform), 1 dynamic (follows the Transform)
  clipBoxes:
    Slot0: { Box: {fileID: <box component>}, Role: 0 }   # Role 0 Keep, 1 Subtract; Box 0 = unused slot
    Slot1: ... Slot2: ... Slot3: ...
  data:
    Color: { rgba: 4294954073 }     # packed RGBA8, A (high byte) = extra 0-1 intensity multiplier
    Intensity: 1
    Range: 10
    InnerAngleDegrees: 50           # full angles
    OuterAngleDegrees: 60

# LinkedFakeLight (needs a Light component on the same GameObject)
  overrideStore, syncTransform, clipBoxes   # as above
  syncMode: 0                       # 0 Every Frame, 1 Editor Only
  intensityMultiplier: 1

# FakeLightClipBox
  m_Enabled: 1
  overrideStore: {fileID: 0}
  syncTransform: 0
  shape: { Center: {...}, Size: {...} }     # in the box's Transform space
```

The packed `rgba` value is a 32-bit integer: red is the lowest byte, alpha the highest (4294954073 = 0xFFFFCC59:
R 0x59, G 0xCC, B 0xFF, A 0xFF).

A `Light` component (`--- !u!108`) has `m_Type` (0 Spot, 1 Directional, 2 Point, 3 Area/Rectangle) and `m_Enabled`.
A GameObject's `m_IsActive: 0` deactivates everything on it; a parent's inactive state also counts.

## Shader variant stripping settings

`ProjectSettings/FulumaSettings.asset` (Project Settings > Fuluma):

```yaml
  stripping:
    mode: 0                         # 0 Automatic (keep what the build's features use), 1 Custom
    countInactiveSources: 0         # Automatic: also keep what disabled features use
    collections: []                 # shader variant collections whose variants are kept too
    denied:                         # Custom: values NOT ticked, per keyword group
    - schemaId: spotLight
      groupId: lampGlare
      valueIds:
      - _                           # "_" = the group's no-keyword value (here: Lamp Glare None, the default)
    exclusions:                     # both modes: every variant with all these values is stripped
    - schemaId: spotLight
      values:
      - groupId: clipClass
        valueId: _FULUMA_CLIP_BOXES
```

Spot light groups and their values (`_` = none): `depthSource` (`_`, `_DEPTH_AS_INPUT_ATTACHMENT`,
`_DEPTH_AS_INPUT_ATTACHMENT_MSAA`, `_DEPTH_LOW_RES`, `_DEPTH_NONE`), `nearPlaneFade` (`_`, `_FULUMA_NEAR_FADE`),
`clipClass` (`_`, `_FULUMA_CLIP_BOX_SINGLE`, `_FULUMA_CLIP_BOX_PAIR`, `_FULUMA_CLIP_BOX_UNION`,
`_FULUMA_CLIP_BOX_EXCLUDE`, `_FULUMA_CLIP_BOXES`), `wideConePrecision` (`_`, `_FULUMA_WIDE_CONES`), `scatteringPhase`
(`_`, `_FULUMA_PHASE_HG`), `distanceFalloff` (`_`, `_FULUMA_FALLOFF_UNITY`), `lampGlare` (`_`, `_FULUMA_POINT_LAMP`).
Light buffer: `upsample` (`_` = Bilinear, `_FULUMA_CHECKERBOARD` = Depth Aware). For `depthSource`, `_` is the depth
texture path. Ids can differ in other package versions: the package's
`Editor/Scripts/Build/FulumaShaderKeywords.cs` is the source.
