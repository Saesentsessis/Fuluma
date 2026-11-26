# Unity 2022.3 and URP Compatibility Mode support

Status: in progress on branch `dev/urp_compatibility_mode` (from `dev/main` at `8922a60`); decisions at the end
(2026-11-25). Design reviewed by a second model the same day; its findings are folded in below. Written after a user
reported Fuluma 0.1.0 running on 2022.3. It doesn't: on a fresh 2022.3.62f3 project with the OpenUPM install, the
Package Manager logs a resolution error (URP 14.0.12 resolved, 17.3.0 requested), installs anyway, and Fuluma's
runtime assembly fails with 12 distinct errors, all from the missing `UnityEngine.Rendering.RenderGraphModule`.

## Goal

One package that draws on:

- **Unity 2022.3 / URP 14**, where a renderer feature can only use the old `Execute` path (URP 14's RenderGraph is
  internal: `RecordRenderGraph` is `internal virtual`, its switch `enableRenderGraph` is internal);
- **Unity 6.0–6.2 / URP 17.0–17.2** in both modes: RenderGraph (the default) and Compatibility Mode (the checkbox under
  Project Settings > Graphics > URP > Render Graph);
- **Unity 6.3 / URP 17.3** in both modes, Compatibility Mode only with Unity's `URP_COMPATIBILITY_MODE` define;
- **Unity 6.4+** as today, RenderGraph only.

The old path is called the **compatibility path** below, whichever Unity version runs it.

## Progress

| Phase | State |
|---|---|
| 1. Groundwork (partials, shared draw loop, packed prepass camera state, compile switches) | Done, `f190b6b` |
| 2. RenderGraph on 6.0–6.2 (API gates, package minimums) | Done, `f190b6b` |
| 3. Compatibility path, full resolution | Done, `7d682f6` |
| Review fixes, 6.3 define support | Done, `7c6a4bf` |
| 2022.3 Editor startup crash (Burst) | Fixed |
| 4. Compatibility path, light buffer | Done, `91d8f32` |
| 5. Compatibility path, early pass | Done, `5c65f17` |
| 6. Test projects (URP asset with the feature in all four), CI matrix | Done |
| Backbuffer depth load (URP, RenderGraph): RenderGraph's extra fragment threads explained | Done, see "Cost on phones" |
| Efficiency fixes: no light buffer without lights, no needless depth copy, depth-only cameras, cached device queries | Done, see "Efficiency fixes" |
| Composite merge experiment (RenderGraph) | Kept on branch `dev/urp_compatibility_mode_composite_merge` (`5ba5116`), not merged (user's choice): it removes RenderGraph's extra LargeOnly reload but not its cost; see "Cost on phones" |
| 7. Release 0.2.0: installer, docs, CHANGELOG | |
| Profiling project on 2022.3 (git-ignored), players, phone measurements | After the suite passes on 2022.3 and 6000.0 |

EditMode suite (304 tests, 36 skipped by design: benchmarks, GPU-less cases), after phase 5:

| Editor / mode | Passed | Failed |
|---|---|---|
| 6000.7.0b4 (dev project), RenderGraph | 268 | 0 |
| 6000.3.0f1 (`Unity-Tests/6000.3.1f1`), RenderGraph | 268 | 0 |
| 6000.0.85f2, RenderGraph | 268 | 0 |
| 6000.0.85f2, Compatibility Mode | 268 | 0 |
| 2022.3.62f3 | 268 | 0 |

Both paths on 6.0–6.3 are covered by running the suite once per mode (decision 13). Compiles without Fuluma errors or
warnings on 6000.7.0b4, 6000.3.0f1 (also with `URP_COMPATIBILITY_MODE`), 6000.0.85f2, 2022.3.62f3 and 2022.3.20f1
(which resolves URP 14.0.10, Burst 1.8.25, Collections 2.6.2, Mathematics 1.3.3).

## Cost on phones (2026-11-25)

The stress scene with 1024 frozen lights (433 drawn), MSAA 1, four players, two alternating rounds of 22 s runs (rounds
within 0.5-1%), GPU cycles per frame in millions (Mali counters, mean of the last 5 frames, both rounds). Players: the
2022.3 profiling project (`M:/Unity/Projects/Fuluma2022`), the 6000.0 one (`M:/Unity/Projects/Fuluma6000`, one
player per mode) and the dev project (6000.7.0b4). No throttling (thermal status 0 throughout).

| Xiaomi 15C (Mali-G52 MC2, 1600x720) | 2022.3 Compat. | 6000.0 Compat. | 6000.0 RenderGraph | 6000.7 RenderGraph |
|---|---|---|---|---|
| Full resolution | 78.25 | 80.00 | 80.18 | 82.97 |
| Light buffer 0.5, all lights | 26.47 | 27.81 | 27.99 | 29.06 |
| Light buffer 0.5, LargeOnly | 45.11 | 46.93 | 47.72 | 49.22 |

| Xiaomi 11T (Mali-G77 MC9, 2400x1080) | 2022.3 Compat. | 6000.0 Compat. | 6000.0 RenderGraph | 6000.7 RenderGraph |
|---|---|---|---|---|
| Full resolution | 15.23 | 16.01 | 16.19 | 15.88 |
| Light buffer 0.5, all lights | 5.67 | 6.16 | 6.25 | 6.24 |
| Light buffer 0.5, LargeOnly | 9.43 | 10.03 | 10.40 | 10.16 |

- **The paths cost nearly the same in one Unity version** (6000.0, RenderGraph vs Compatibility): 15C +0.2%, +0.6%,
  +1.7%; 11T +1.1%, +1.5%, +3.7%. The first comparison (2022.3 Compatibility vs 6000.7 RenderGraph) measured Unity
  versions, not paths.
- **2022.3 -> 6000.0 (both Compatibility): +2-3% (15C), +5-9% (11T).** About one more full screen of pixels written per
  frame (15C 5.3 M -> 6.4 M, 11T 10.5 M -> 13.0 M) and more external writes (15C +7 MB, 11T +6 MB): URP 17 does more
  full-screen work around the frame, whatever the path.
- **6000.0 -> 6000.7 (both RenderGraph): +3-4% on the 15C, -2% to 0% on the 11T.** On the G52 shader fragment cycles rise
  3-5% in every configuration, including the light buffer at 0.5 where the depth input attachment isn't used, with ALU
  issue cycles about the same: likely 6000.7's shader code generation on Bifrost (not confirmed; Mali's offline
  compiler is unavailable here). On the G77 it doesn't show, and at full resolution 6000.7's depth input attachment
  saves URP's depth copy (11T: 7.8 M vs 13.1 M pixels; 15C: 4.1 M vs 6.4 M).
- Mali's "Pixels" counter behaves like the area of the tiles each render pass processes, not shaded pixels: an extra
  full-screen render pass adds one screen of pixels even when it draws nothing.
- **RenderGraph-only: LargeOnly writes one extra full screen of pixels** (15C +1.17 M = 1600x720, 11T +2.6 M =
  2400x1080) compared with Compatibility in 6000.0; most of RenderGraph's LargeOnly difference (11T +3.7%). Found with
  RenderDoc captures of 6000.0 Windows Vulkan players at 1600x720 (`Fuluma6000/Builds/Win_Off`, `Win_On`): both frames
  have the same draws; the difference is a render pass. The depth-aware composite samples the camera's depth
  attachment as a texture, so RenderGraph runs it in a render pass of its own (camera color, no depth). URP's
  `DrawTransparentObjects` then needs color and depth, so RenderGraph begins another render pass, reloading both: on a
  tile GPU a full screen of load work, which Mali counts as fragment threads and pixels. The stress scene has no
  transparent objects; on the Compatibility path Unity's Vulkan backend doesn't begin a render pass for that empty
  pass, so it never pays the reload. With all lights in the buffer the same split exists, but there Compatibility pays
  for URP's depth copy (it always asks for the depth texture) and RenderGraph doesn't, so they even out; with
  LargeOnly both make the copy (the small full-resolution lights read it).
  - In a scene with transparent objects both paths begin that pass, so the difference shows only when the
    transparents pass is empty.
  - Tried on branch `dev/urp_compatibility_mode_composite_merge` (`5ba5116`, not merged): where URP makes
    `_CameraDepthTexture` anyway (MSAA, LargeOnly without depth input attachment) the downsample and the depth-aware
    composite read the copy, and the composite keeps the camera's depth attached read-only. RenderGraph can't merge a
    pass into one that sampled its target (`PassBreakReason.NextPassTargetsTexture` in Core 17.0), so this lets it
    merge. RenderDoc (6000.0 Vulkan): transparents then follow the composite as `vkCmdNextSubpass`, and an extra empty
    raster pass from another feature before them (`-fulumaExtraPass` in `Fuluma6000`) joins that subpass too. Suite
    268/0 on 2022.3, 6000.0 (both modes), 6000.7. Phones, both players rebuilt from the same state, two rounds:
    LargeOnly pixels now match Compatibility (11T 19.62 M -> 17.02 M vs 17.0 M; 15C 8.89 M -> 7.72 M vs 7.7 M), GPU
    cycles unchanged (11T 10.40 M -> 10.39 M; 15C 47.72 M -> 47.84 M): the reload is cheap on Mali. Not merged because
    of that and its trade-off: depth written after URP's copy (another feature's geometry between the copy and Fuluma's
    pass) no longer reaches the light buffer.
  - The experiment didn't change cycles because the depth reload only moved: the composite's merged pass now loads the
    camera depth at its start. The cost is the depth load itself (next bullet), not the extra render pass.
- **RenderGraph's extra fragment threads are depth loads, and the first one is URP's, not Fuluma's** (found
  2026-11-25). On Mali a render pass that loads a depth/stencil attachment runs a full screen of fragment threads that
  sample it through the texture unit (Bifrost and Valhall preload the tile buffer with "frame shaders"; see Mesa's
  Panfrost driver). Color loads showed no such cost here. Every RenderGraph frame on 6000.0 has one depth load more
  than Compatibility, LargeOnly frames two:
  - **The backbuffer depth.** URP's "Draw UIToolkit/uGUI Overlay" pass (`DrawScreenSpaceUIPass.RenderOverlay`) always
    attaches the backbuffer depth `ReadWrite`, even with no canvas in the scene, and RenderGraph merges it into the
    final blit's render pass. With an intermediate texture (here HDR, so for any such URP project) the backbuffer is
    imported without `clearOnFirstUse`, so the native pass compiler loads it (`LoadReason.LoadImported`). The final
    blit's color is a partial `Write` too (Load), but that one costs nothing. In Compatibility Mode the engine draws the
    UI after URP, and the final blit pass starts with Don't Care. URP 17.7 (6000.7) imports the backbuffer depth the
    same way; its final blit is `WriteAll`.
  - **The camera depth after the depth-aware composite** (LargeOnly; see above).

  Experiment: URP 17.0.4 embedded in `Fuluma6000` with two launch switches, `-urpFinalBlitWriteAll` (final blit color
  Don't Care) and `-urpClearBackbufferDepth` (backbuffer depth `clearOnFirstUse`). Checked with RenderDoc on Windows
  Vulkan: each switch changes only its own load action. Then on the phones (6000.0, the same stress setup as above, two
  rounds; GPU cycles and fragment threads per frame in millions):

  | 11T (Mali-G77) | Compat. | RenderGraph | + final blit WriteAll | + backbuffer depth cleared | both |
  |---|---|---|---|---|---|
  | Full resolution, cycles / threads | 16.04 / 30.23 | 16.16 / 32.86 | 16.14 / 32.89 | 16.04 / 30.24 | 16.02 / 30.24 |
  | Light buffer 0.5, all lights | 6.16 / 22.54 | 6.25 / 25.19 | 6.24 / 25.22 | 6.02 / 22.55 | 6.02 / 22.53 |
  | Light buffer 0.5, LargeOnly | 10.02 / 30.21 | 10.41 / 35.38 | 10.42 / 35.42 | 10.18 / 32.75 | 10.20 / 32.74 |

  | 15C (Mali-G52) | Compat. | RenderGraph | + final blit WriteAll | + backbuffer depth cleared | both |
  |---|---|---|---|---|---|
  | Full resolution, cycles / threads | 79.99 / 15.85 | 80.12 / 17.87 | 79.94 / 17.74 | 79.82 / 15.91 | 79.97 / 15.84 |
  | Light buffer 0.5, all lights | 27.78 / 10.91 | 27.84 / 13.76 | 27.96 / 13.70 | 27.87 / 11.87 | 27.67 / 11.78 |
  | Light buffer 0.5, LargeOnly | 47.05 / 15.51 | 47.63 / 19.51 | 47.79 / 19.41 | 47.71 / 17.59 | 47.47 / 17.49 |

  - Clearing the backbuffer depth removes one depth load. Fragment threads drop by one screen on the 11T (2.6 M) and
    by 1.9 M on the 15C; texture issue cycles drop by 0.64 M and 0.6 M. On the 11T, GPU cycles drop 0.8% (full), 3.7%
    (all lights in the buffer) and 2.2% (LargeOnly). On the 15C the change is within noise (±0.4%).
  - With it, RenderGraph matches Compatibility at full resolution on both phones. With all lights in the buffer,
    RenderGraph is 2.3% cheaper on the 11T: Compatibility pays for URP's depth copy, RenderGraph doesn't. LargeOnly
    keeps the camera depth reload: +1.6% (11T) and +0.9-1.4% (15C), with 2.5 M (11T) and 2.1 M (15C) more threads.
  - The second reload exists only when nothing after the composite needs the camera targets. In a scene with
    transparent objects, Compatibility reloads too.
  - Fuluma can't change the backbuffer import: it lives in `UniversalRenderer.CreateRenderGraphCameraRenderTargets`. A Unity bug report is
    the proper route; until then this cost applies to any URP RenderGraph project on Mali, with or without Fuluma.
- The overlay's per-pass GPU timers (`ProfilingScope`) disagree with the counters on these tile GPUs (the same frame
  shows a 12.3 ms downsample on Compatibility and 1.1 ms on RenderGraph): use the counters.
- Straight-to-screen cameras on the compatibility path needed the composite flip (fixed in `046aff2`, verified on the
  15C with HDR and scene depth off).

## Efficiency fixes (2026-11-26)

From a review of the branch's code (no per-frame managed allocations found):

1. **No light buffer without lights.** The downsample, clear and composite are whole-screen work, and were recorded
   whether or not the camera saw a light.
   - Compatibility path: `Execute` fills its command buffer right before it runs, so it now finishes the camera's culling
     first and records only what it found. LargeOnly decides from this frame's culling (was the previous frame's).
   - RenderGraph: recording precedes culling, so the previous frame decides, as LargeOnly already did. Without
     LargeOnly the full-resolution pass stands in only where it has scene depth without an extra copy (no scene depth,
     depth input attachment, or the depth texture already requested for MSAA). Elsewhere the buffer is still recorded:
     on 6000.0-6000.5 a stand-in would need URP's depth copy, which costs as much as it saves.
2. **No depth copy for the Compatibility light buffer when it can sample the depth attachment.** Requested only for
   full-resolution draws, MSAA, or cameras not certain to render into an intermediate depth texture. Certain: base
   cameras without a target texture, outside the Scene view, with URP 17's `requiresIntermediateTexture` (set with the
   light buffer) or, on URP 14, HDR or the opaque texture. A wrong guess (the attachment turns out unsampleable) skips
   that camera's buffer once, warns, and requests the copy from then on.
3. Depth-only cameras (target texture in a depth format) get neither culling nor the pass.
4. `UrpDepthPrepass` queries the device's capabilities once, not per camera.

Measured on the phones (6000.0, the stress setup above, two rounds, spread under 0.6%): the Compatibility player with
the fixes against the same player built from the commit before them. GPU active cycles per frame, millions:

| | 11T before | 11T after | 15C before | 15C after |
|---|---|---|---|---|
| Camera sees no light (lights in the store) | 1.96 | **0.89** (-55%) | 5.93 | **2.73** (-54%) |
| Light buffer 0.5, all lights | 6.14 | **5.82** (-5.3%) | 27.82 | **27.26** (-2.0%) |
| Light buffer 0.5, LargeOnly | 10.02 | 9.99 | 46.98 | 47.01 |
| Full resolution | 16.04 | 16.03 | 79.70 | 79.89 |

- The light buffer run's saving is URP's depth copy: 2.6 M fewer fragment threads on the 11T. With it, Compatibility
  is now cheaper than RenderGraph there (6.24 / 27.98 M cycles), which still pays URP's backbuffer depth load.
- 2022.3 against the 2026-11-25 runs (other day, same player setup): light buffer 0.5 5.67 -> 5.34 (11T) and
  26.47 -> 25.89 (15C); full resolution and LargeOnly unchanged.
- Render thread per frame (11T): no light 1.02 -> 0.66 ms, light buffer 1.14 -> 1.04 ms.
- Screenshots match the earlier player's to 0.1% of lit pixels.

**Cameras of different sizes (the shared light buffer targets, decision 4).** 2, 4 and 8 split-screen cameras, all the
same size or every other one 8 pixels narrower, so the Compatibility path reallocates its three targets at every camera
switch (`-fulumaCameras`, `-fulumaCameraSizes` in `Fuluma6000`). No measurable penalty on either phone: mixed and same
sizes agree within the runs' spread in GPU cycles (11T 6.46/6.46, 9.15/9.17, 12.55/12.60 M; 15C 27.06/27.11,
35.54/35.58, 37.63/37.67 M) and render-thread time (at most +0.06 ms with 8 cameras, within noise). URP's own
per-camera reallocation (full resolution, control) shows nothing either. The shared set stays.

## Compatibility Mode by version

Sources: Unity's 6.2/6.3 upgrade guides, the 6000.4.0a4 release notes, the official "Render Graph Updates in Unity
6.3" post, and the installed URP 14.0.12, 17.0.4, 17.3.0 and 17.7.0 source.

| Unity / URP | Old path |
|---|---|
| 2022.3 / 14 | The only path for features. |
| 6.0–6.2 / 17.0–17.2 | Deprecated checkbox, chosen at runtime (`RenderGraphSettings.enableRenderCompatibilityMode`, settable from code; changing it recreates the pipeline, `[RecreatePipelineOnChange]`). Old members are `[Obsolete]` warnings. `SetupRenderPasses` deprecated in 6.2. |
| 6.3 / 17.3 | Compiled only with the scripting define `URP_COMPATIBILITY_MODE`, "not recommended and not supported" for shipping; with it, the setting works as on 6.0–6.2. Without it the old members are empty stubs (`Runtime/Deprecated.cs`): code compiles, nothing runs. |
| 6.4+ / 17.4+ | Removed. Overriding `Execute` is CS0115. |

**Consequence:** a minimum below 6000.3 also admits 6.0–6.2, so the RenderGraph path must compile and work there too.
That is what failed on 6000.0.0f1 on 2026-11-20 and why the minimum became 6000.3.

API availability (checked in installed sources, and on Unity's Graphics repository branches for 6000.1/6000.2):

| API | Available |
|---|---|
| `RenderingMode.DeferredPlus` | 6000.1+ |
| `UniversalRendererData.prepassLayerMask` | 6000.2+ (earlier, the prepass draws the opaque layers) |
| `TextureDesc.colorFormat` | every Unity 6 version (not obsolete); `.format` is missing on early 6000.0 patches |
| `requiresIntermediateTexture` | 6000.0+ (URP 14 has none) |
| `GraphicsFormatUsage` / `GraphicsDeviceType.WebGPU` | 6000.0+ (2022.3: `FormatUsage`, no WebGPU) |
| `UniversalRenderPipelineAsset.rendererDataList` | 6000.0+ (URP 14 keeps `m_RendererDataList` internal) |
| depth input attachment API | 6000.6+ |

## Design

### Compile switches

Same pattern as the lifecycle attributes, at the top of each file:

```csharp
// RenderGraph path: every Unity 6 version.
#if UNITY_6000_0_OR_NEWER
#define FULUMA_RENDER_GRAPH
#endif
// Compatibility path: 2022.3, 6.0–6.2, and 6.3 with Unity's define (decision 3).
#if !UNITY_6000_3_OR_NEWER || (URP_COMPATIBILITY_MODE && !UNITY_6000_4_OR_NEWER)
#define FULUMA_COMPATIBILITY_PATH
#endif
```

Unity defines suffice because URP is locked to the Editor version in 2022.3 and Unity 6. Unity 2023.1/2023.2
(URP 15/16) take the 2022.3 code everywhere. Compatibility-path code sits inside `#pragma warning disable 618, 672`
(obsolete member, overriding an obsolete member), as in Unity's own `BlitToRTHandlePass` sample.

On 6.0–6.3 both paths compile into the same pass class; URP calls `Execute` or `RecordRenderGraph` depending on the
mode. The pass reads the mode once, when it is created (`FulumaSpotLightRenderPass.CompatibilityModeActive` →
`UsesCompatibilityPath`): a mode switch recreates URP's pipeline, which disposes and re-creates every feature's pass.

### Shared core, two front ends

| File | Contents |
|---|---|
| `FulumaSpotLightRenderPass.cs` | Settings, materials, culling, camera preparation (`TryBeginPreparingCamera`, `FinishPreparingCamera`, `HasNeededDepthTexture`), the draw loop (shared). |
| `FulumaSpotLightRenderPass.RenderGraph.cs` | `RecordRenderGraph`, `RecordLightBufferBeforeOpaques`, raster pass data (`FULUMA_RENDER_GRAPH`). |
| `FulumaSpotLightRenderPass.Compatibility.cs` | `Execute`, `ExecuteLightBufferBeforeOpaques` (`FULUMA_COMPATIBILITY_PATH`). |
| `DownsampledLightBuffer.cs` + `.RenderGraph.cs` / `.Compatibility.cs` | Same split; shared formats, sizes, material; `OnCreated` / `OnDisposing` partial methods for a path's own resources. |
| `UrpDepthPrepass.cs` | Reads a packed `CameraState` (flags byte, MSAA byte), filled from `UniversalCameraData` or `CameraData`. |
| `Editor/Scripts/Core/UrpRendererDataList.cs` | A pipeline asset's renderers: `rendererDataList` on Unity 6, the serialized list through `SerializedObject` on URP 14 (no reflection). |

The draw loop is generic over a private struct wrapper, so neither path boxes or allocates:

```csharp
private interface IDrawCommands
{
	void DrawMeshInstancedProcedural(Mesh mesh, int submesh, Material material, int shaderPass, int count,
		MaterialPropertyBlock properties);
}
// RasterDrawCommands wraps RasterCommandBuffer (RenderGraph), CommandBufferDrawCommands wraps CommandBuffer.
private static void DrawPool<TCommands>(TCommands cmd, ...) where TCommands : struct, IDrawCommands
```

### Compatibility path, pass by pass

URP 14 calls a pass's `Execute` once per camera and submits each camera before the next, so material state set while
a camera executes holds for its draws. Camera targets (`cameraColorTargetHandle` / `cameraDepthTargetHandle`) are
readable inside `Execute`; the feature needs no `SetupRenderPasses`. Every third-party feature pass turns off URP 14's
native render pass for the camera (`ScriptableRendererFeature.SupportsNativeRenderPass()` is internal and false), so
switching targets inside `Execute` is legal; the cost is that Fuluma always breaks URP 14's native pass chain.

1. **Full resolution draw** (done): draws into the camera targets URP binds for a pass that configures none. Scene
   depth from `_CameraDepthTexture` (`ConfigureInput(Depth)` for every draw; URP 14 moves its depth copy before the
   earliest pass that asks), resolved with MSAA; `_DEPTH_NONE` unchanged. Matches the RenderGraph path on 6.3–6.5.
2. **Light buffer** (phase 4):
   - three persistent RTHandles (light color B10G11R11/RGBA16F, depth, R32F linear depth), reallocated from
     `cameraTargetDescriptor` before any command for the camera (`RenderingUtils.ReAllocateIfNeeded` on URP 14,
     `ReAllocateHandleIfNeeded` on URP 17); one shared set (decision 4);
   - one `Execute` runs downsample → low-resolution draw → composite, switching targets with `CoreUtils.SetRenderTarget`
     (DontCare loads on the low-resolution targets), then restores the camera targets (URP 14 skips a
     `SetRenderTarget` when its cached attachments match);
   - the downsample and the depth-aware composite read `_CameraDepthTexture` by name
     (`RenderTargetIdentifier` of the property, a globally set texture resolved at execution), bound with
     `cmd.SetGlobalTexture`; the buffer's own material is shared with the RenderGraph path (a mode switch recreates it);
   - **backbuffer cameras on URP 14** (no intermediate texture: Intermediate Texture Auto without HDR, post-processing,
     MSAA or render scale, and no depth request): the target's projection is flipped relative to the low-resolution
     render targets. The composite's scale/bias and the low-resolution draw's projection handle the flip
     (`cameraData.IsRenderTargetProjectionMatrixFlipped`; decision 9). 6.0–6.3 honour `requiresIntermediateTexture`, so
     this case is URP 14 only;
   - LargeOnly works as now (same culling buckets, same "last frame had large lights" rule).
3. **Early pass** (phase 5, `AfterRenderingPrePasses`): BeforeOpaques works the same way in URP 14
   (`ConfigureInput(Depth)` on a pass before the main rendering forces the prepass). With depth priming, URP 14 and 17
   copy the primed depth into `_CameraDepthTexture` at the same event, after Fuluma's pass, so the early pass reads the
   primed depth attachment then (as the RenderGraph path does), otherwise `_CameraDepthTexture`. **Auto** ports
   `UrpDepthPrepass` to URP 14's rules (depth priming, `copyDepthMode`, `opaqueLayerMask`), re-derived from URP 14's
   `UniversalRenderer.Setup`; where the decision can't be derived, Auto falls back to after-opaques.
4. **Cameras skipped**: cameras rendering into a depth-only target (URP 14 gives them no fresh depth texture).
5. **Feature**: `FindRendererData` on URP 14 uses `Resources.FindObjectsOfTypeAll<UniversalRendererData>()` (a feature
   is a sub-asset of its renderer data); `AddRenderPasses` retries it at most once per pass (it allocates).
6. **Shaders**: compile every variant against URP 14 on D3D11, Vulkan and Metal (GLES 3 is unsupported anyway). The
   `_DEPTH_AS_INPUT_ATTACHMENT*` variants are unreachable before 6000.6; make sure the Automatic stripping rules know
   that, so builds don't keep them.

### Feature parity on the compatibility path

| Feature | Compatibility path |
|---|---|
| Culling, LODs, clip boxes, light models, near fade, wide cones, intensity | Same (shared CPU code and shaders). |
| Forward, Forward+, Deferred | Yes. |
| Deferred+ | No on URP 14; check whether 6.1–6.3 allow it in Compatibility Mode. |
| Light buffer (scale < 1, All / LargeOnly, both upsamplers) | Yes; scene depth through `_CameraDepthTexture` (costlier on tile GPUs, plus a load of the camera targets after the buffer). |
| Light buffer timing BeforeOpaques | Yes. Auto: yes where the URP 14 rules port, else after-opaques. |
| Depth input attachment | No (RenderGraph on 6000.6+ only). |
| Native render pass merging | No: every feature pass turns it off on URP 14. |

## Package, CI and docs

- `package.json` (done): `"unity": "2022.3"`, `"unityRelease": "20f1"`, URP 14.0.10 (what 2022.3.20f1 bundles, with
  Core 14.0.10). Burst 1.8.25, Collections 2.6.2 and Mathematics 1.3.3 stay. Unity 6 projects keep their bundled URP 17,
  because the dependency is only a minimum. Version 0.2.0 at release.
- README: Unity 6000.0 from 6000.0.85f2 (the tested patch); 2023.1/2023.2 expected to work, untested (decision 10).
- Test projects: recreate `Unity-Tests/2022.3.62f3` (removed 2026-11-19) and add `Unity-Tests/6000.0.85f2`, both with
  URP, MeshGen, the OpenUPM registry and Fuluma in `testables`. CI matrix: 2022.3.62f3, 6000.0.85f2, 6000.3.1f1,
  6000.6.1f1. The repo's test projects reference a pipeline asset that isn't there, so render tests `Assert.Ignore`
  in them (CI covers CPU tests only); worth fixing now that there are two paths.
- `release.yml`: `INSTALLER_UNITY_VERSION` 2022.3.62f3, Installer project migrated down (as done for ShaderWorks).
- The dev project stays on 6000.7, where the compatibility path isn't compiled at all. Scratch projects at short paths
  (`%TEMP%/claude/s22`, `s60`, `s63`) carry an embedded copy of the package, refreshed before each run: opened by
  `file:` path, 2022.3 rewrote the repository's guid-only `.meta` files into full importer blocks, and a long project
  path broke the package cache (260-character limit).
- **Editor startup on 2022.3** (fixed): when Unity loads a project whose scripts are already compiled, URP creates its
  renderers, and so Fuluma's feature, while assets load, before Burst's Editor compiler is initialized; the first call
  of a Burst function (MeshGen, building the proxy mesh) crashed the Editor (`BurstCompilerService::CompileAsync`).
  Confirmed by running with `--burst-disable-compilation` (no crash); 6000.0 doesn't crash. In the 2022.3 Editor the
  feature's first `Create` after a domain load now waits for `EditorApplication.delayCall` (user's choice). Not
  checked: a scene with fake light components open at startup (their registration calls Burst `PackingKernels`); in
  batch mode Unity doesn't restore the last scene, so it needs a check in the Editor.
- Docs: README Requirements, Platform Support and Upcoming Changes; `Documentation~/FulumaRenderFeature.md` ("RenderGraph
  only"); CHANGELOG; the project's CLAUDE.md.

## Tests

- **Both paths in one Editor** on 6.0–6.3: run the render fixtures once per mode, toggling
  `RenderGraphSettings.enableRenderCompatibilityMode` and restoring it; render once after the switch (the pipeline is
  recreated) and re-read `Feature.SpotLightRenderPass`. Add a parity test: the same scene through both paths, images
  equal within tolerance. The first thing it proves is the by-name `_CameraDepthTexture` binding.
- 2022.3: the whole suite on the compatibility path. Tests reading RenderGraph-only internals get path-neutral
  equivalents.
- Not covered by Editor tests (render into a RenderTexture): backbuffer cameras, camera stacks, XR arrays; check them
  in a Game view or player.
- Lifecycle on 2022.3: play-mode switches with domain reload off (the pre-6000.5 fallback runs there for real).
- Players on 2022.3: Windows D3D11/D3D12, Android Vulkan on the Mali-G52 and G77 phones. Measure the light buffer's
  fixed cost against the RenderGraph path on 6000.3.

## Risks and unknowns

- **Several cameras of different sizes** (Scene view + Game view, split screen) reallocate the light buffer's RTHandles
  every camera with one shared set (decision 4). Measured on phones with up to 8 cameras: no measurable cost (see
  "Efficiency fixes").
- **Auto timing on URP 14:** porting the prepass rules may miss cases; the early pass then silently doesn't run. The
  image stays correct and only the saving is lost.
- **Untested compiled code:** on 6.0–6.3 the compatibility path compiles but most users never run it; only the
  dual-path fixtures cover it.
- **6.1 and 6.2** compile the same gated code but aren't tested locally or in CI.
- **Unknowns:** whether Deferred+ works in Compatibility Mode on 6.1–6.3; whether every shader variant compiles with
  URP 14's includes on Vulkan and Metal.

## Decisions (user's, 2026-11-25)

1. **Minimum: 2022.3.20f1** (`"unity": "2022.3"`, `"unityRelease": "20f1"`, Collections 2.6.2's floor), and with it
   6.0–6.2 on RenderGraph. 2022.3.20f1 is installed once to check that the package resolves and compiles; CI and the
   test project use 2022.3.62f3.
2. **Compatibility Mode on 6.0–6.2: supported.** Render tests run once per mode there, plus the parity test.
3. **6.3 with `URP_COMPATIBILITY_MODE`: supported** (first decided against, reversed after the review). 6.4+ is
   RenderGraph only.
4. **Light buffer targets on the compatibility path: one shared set**, reallocated when the camera size changes.
5. **Version 0.2.0.**
6. **Editors:** 6000.0 LTS only (6000.0.85f2, the newest the license covers; 6000.0.86f1 needs extended LTS).
7. **Commits:** a local commit per phase, never pushed, in the repository's commit style.
8. **Players:** a git-ignored 2022.3 profiling project for desktop and phone measurements, only after the suite
   passes on 2022.3 and 6000.0.
9. **Backbuffer cameras on URP 14:** handle the flip (composite and low-resolution projection), not a full-resolution
   fallback.
10. **README:** 6000.0 from 6000.0.85f2; 2023.1/2023.2 expected to work, untested.
11. **Review fixes applied:** primed depth in the early pass, renderer lookup retried once per pass, one light buffer
    material, mode cached per pass, DontCare loads, depth-only cameras skipped, member order and doc corrections, the
    6000.0 suite in both modes before phase 4. `_earlyDepthIsAttachment` stays shared: the compatibility early pass
    uses it too.
12. **2022.3 Editor startup crash:** defer the feature's first `Create` to `EditorApplication.delayCall` (2022.3 Editor
    only; Unity 6 and players unchanged).
13. **Test coverage:** all four `Unity-Tests` projects get a URP asset whose renderer has the feature (render tests run
    wherever there is a GPU; CI runners have none, so they skip there); on 6.0–6.3 the suite runs once per mode, no
    in-suite parity test; CI matrix 2022.3.62f3, 6000.0.85f2, 6000.3.1f1, 6000.6.1f1; the profiling project lives
    outside the repository.
