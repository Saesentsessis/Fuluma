# Unity 2022.3 and URP Compatibility Mode support (plan)

Status: in progress on branch `dev/urp_compatibility_mode` (from `dev/main` at `8922a60`), decisions taken 2026-11-25
(see the end). Written after a user reported Fuluma 0.1.0
running on 2022.3; it doesn't: on a fresh 2022.3.62f3 project with the OpenUPM install, the Package Manager logs a
resolution error (URP 14.0.12 resolved, 17.3.0 requested), installs anyway, and Fuluma's runtime assembly fails with 12
distinct errors, all from the missing `UnityEngine.Rendering.RenderGraphModule`.

## Goal

One package that draws on:

- **Unity 2022.3 / URP 14**, where a renderer feature can only use the old `Configure`/`Execute` path (URP 14's
  RenderGraph is internal: `RecordRenderGraph` is `internal virtual`, its switch `enableRenderGraph` is internal);
- **Unity 6.0–6.2 / URP 17.0–17.2** in both modes: RenderGraph (the default) and Compatibility Mode (the checkbox under
  Project Settings > Graphics > URP > Render Graph);
- **Unity 6.3+** as today, RenderGraph only.

The old path is called the **compatibility path** below, whichever Unity version runs it.

## Compatibility Mode by version

Sources: Unity's 6.2/6.3 upgrade guides, the 6000.4.0a4 release notes, the official "Render Graph Updates in Unity
6.3" post, and the installed URP 14.0.12, 17.3.0 and 17.7.0 source.

| Unity / URP | Old path |
|---|---|
| 2022.3 / 14 | The only path for features. |
| 6.0–6.2 / 17.0–17.2 | Deprecated checkbox, chosen at runtime (`RenderGraphSettings.enableRenderCompatibilityMode`). Old members are `[Obsolete]` warnings. `SetupRenderPasses` deprecated in 6.2. |
| 6.3 / 17.3 | Compiled only with the scripting define `URP_COMPATIBILITY_MODE`, "not recommended and not supported" for shipping. Without it the old members are empty stubs (`Runtime/Deprecated.cs`): code compiles, nothing runs. |
| 6.4+ / 17.4+ | Removed. Overriding `Execute` is CS0115. |

**Consequence:** a minimum below 6000.3 also admits 6.0–6.2, so the RenderGraph path must compile and work there too.
That is what failed on 6000.0.0f1 on 2026-11-20 (no Deferred+, `prepassLayerMask`, `TextureDesc.format`, depth input
attachment API) and why the minimum became 6000.3. Supporting 2022.3 reopens that work.

## What blocks 2022.3 today (measured on 2022.3.62f3, embedded package copy)

- **Runtime:** only the five RenderGraph files: `FulumaSpotLightRenderPass` (1457 lines), `DownsampledLightBuffer`
  (323), `FulumaLightBufferEarlyPass` (34), `UrpDepthPrepass` (102), `FulumaRenderFeature` (817, compiles only once
  the passes do). Without them the whole runtime assembly compiles: store, Burst jobs, culling, GPU mirror,
  components, proxy meshes, the pre-6000.5 lifecycle fallback.
- **Editor:** `StoreFeatureLookup` uses `UniversalRenderPipelineAsset.rendererDataList`, not public in URP 14. Read the
  serialized `m_RendererDataList` through `SerializedObject` instead (no reflection); `rendererFeatures` is public in
  14. `FulumaShaderStrippingSource` and `FulumaSettingsProvider` only failed because the feature was missing.
- **Shaders:** both compile against URP 14 on D3D with warnings only; only default variants were tried
  (`ShaderUtil.CompilePass`).
- **Dependencies:** ShaderWorks, MeshGen and Collections Specialized declare and compile on 2022.3. Collections 2.6.2
  requires **2022.3.20f1**, the effective floor.
- **Tests:** not tried. They don't reference RenderGraph types, but read pass internals that are RenderGraph-specific
  (`LastRecordedLowResolutionPasses`, `LastRecordedLightBufferBeforeOpaques`).

## Design

### Compile switches

Same pattern as the lifecycle attributes, at the top of each file:

```csharp
// RenderGraph path: every Unity 6 version.
#if UNITY_6000_0_OR_NEWER
#define FULUMA_RENDER_GRAPH
#endif
// Compatibility path: 2022.3 and 6.0–6.2 (not 6.3 with Unity's define, decision 3).
#if !UNITY_6000_3_OR_NEWER
#define FULUMA_COMPATIBILITY_PATH
#endif
```

Unity defines suffice because URP is locked to the Editor version in 2022.3 and Unity 6 (no asmdef `versionDefines`
needed). Compatibility-path code on 6.x sits inside `#pragma warning disable 618, 672` (obsolete member, overriding
an obsolete member), as in Unity's own `BlitToRTHandlePass` sample.

On 6.0–6.2 both paths compile into the same pass class; URP calls `Execute` or `RecordRenderGraph` depending on the
mode, so the pass needs no runtime switch for drawing. The feature reads the mode where its setup differs
(`SetupRenderPasses`, intermediate texture), from `GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>()`
on 6.x, and is always in compatibility mode on 2022.3.

### Shared core, two front ends

What the passes do per camera is already path-independent: culling (`BeginCulling` from `OnCameraPreCull`, which URP
14 has), `TryPrepareCamera`'s keyword and material binding, `FillPassData`, the class-by-class draw loop and the light
buffer's materials. The RenderGraph-specific parts are the pass declarations, texture handles and the command buffer
type. The draw loop makes exactly one command call (`DrawMeshInstancedProcedural`), the light buffer one
(`Blitter.BlitTexture`, which has `CommandBuffer` overloads in Core 14 and both overloads in Core 17).

Split:

| File | Contents |
|---|---|
| `FulumaSpotLightRenderPass.cs` | Settings, materials, culling, binding, draw loop (shared). |
| `FulumaSpotLightRenderPass.RenderGraph.cs` | `RecordRenderGraph`, `RecordLightBufferBeforeOpaques`, raster pass data (`FULUMA_RENDER_GRAPH`). |
| `FulumaSpotLightRenderPass.Compatibility.cs` | `OnCameraSetup`, `Execute`, the early-pass entry (`FULUMA_COMPATIBILITY_PATH`). |
| `DownsampledLightBuffer.cs` + `.RenderGraph.cs` / `.Compatibility.cs` | Same split; shared formats, sizes, blit materials. |
| `UrpDepthPrepass.cs` | Takes a small struct of camera/renderer values instead of `UniversalCameraData` / `UniversalResourceData`, filled by either front end. |

The draw loop becomes generic over a command wrapper, so neither path boxes or allocates:

```csharp
internal interface IFulumaDrawCommands
{
	void DrawMeshInstancedProcedural(Mesh mesh, int submesh, Material material, int pass, int count,
		MaterialPropertyBlock properties);
}
// RasterDrawCommands wraps RasterCommandBuffer (RenderGraph), LegacyDrawCommands wraps CommandBuffer.
private static void DrawPool<TCommands>(ref TCommands cmd, ...) where TCommands : struct, IFulumaDrawCommands
```

### Compatibility path, pass by pass

1. **Full resolution draw** (`Execute` at the feature's event): draws into the camera targets URP sets for the pass
   (no `ConfigureTarget` needed). Scene depth from `_CameraDepthTexture` (`ConfigureInput(Depth)`), resolved with MSAA;
   `_DEPTH_NONE` unchanged. No depth input attachment (6000.6+ RenderGraph only anyway), so this matches the
   RenderGraph path on 6.3–6.5. Profiling through `ProfilingScope(cmd, FulumaProfiling.DrawSampler)`.
2. **Light buffer**:
   - three persistent RTHandles in `DownsampledLightBuffer` (light color B10G11R11/RGBA16F, raw depth, R32F linear
     depth; `PickLightFormat`/`PickRawDepthFormat` unchanged), sized in `OnCameraSetup` with
     `RenderingUtils.ReAllocateIfNeeded` (URP 14) / `ReAllocateHandleIfNeeded` (URP 17, the old one is obsolete);
   - one `Execute` runs downsample → low-resolution draw → composite, switching targets with
     `CoreUtils.SetRenderTarget`, then restores the camera targets;
   - the downsample reads `_CameraDepthTexture`. The RenderGraph path reads the depth attachment instead, which cut
     the buffer's fixed cost from ~8.3 M to ~2.5 M cycles on the Mali-G52; reading URP 14's
     `cameraDepthTargetHandle` where it's a single-sampled intermediate is a later optimization;
   - LargeOnly works as now (same culling buckets, same "last frame had large lights" rule).
3. **Early pass** (`AfterRenderingPrePasses`): BeforeOpaques works the same way in URP 14. `ConfigureInput(Depth)` on
   a pass before the main rendering forces the prepass (`GetRenderPassInputs` in URP 14's `UniversalRenderer`).
   **Auto** ports `UrpDepthPrepass` to URP 14's rules: depth priming, `copyDepthMode` and `opaqueLayerMask` (URP 14 has
   no `prepassLayerMask`), all public settings, no reflection. Where URP 14's decision can't be derived, Auto falls back
   to after-opaques.
4. **Feature**: `SetupRenderPasses(renderer, in RenderingData)` hands `cameraColorTargetHandle` /
   `cameraDepthTargetHandle` to the pass. URP 14 only allows reading them there; deprecated in 6.2. URP 14 has no
   `requiresIntermediateTexture`; the compatibility path doesn't need one, because it reads depth from
   `_CameraDepthTexture` and blends into whatever color target is bound. `OnValidate`, `Dispose(bool)` and `Create`
   exist in URP 14 as used.
5. **Editor**: `StoreFeatureLookup` reads the serialized renderer list on every version (one code path).
6. **Shaders**: compile every variant against URP 14 on D3D11, Vulkan and Metal (GLES 3 is unsupported anyway). The
   `_DEPTH_AS_INPUT_ATTACHMENT*` variants are unreachable before 6000.6. Make sure the Automatic stripping rules know
   that, so builds don't keep them.

### RenderGraph path on 6.0–6.2

Gate each API missing there: Deferred+ (`RenderingMode.DeferredPlus`), `prepassLayerMask`, `TextureDesc.format` (an
older field name, presumably `colorFormat`). The depth input attachment is already behind `UNITY_6000_6_OR_NEWER`.
Which minor added each is unknown. Find out by compiling on 6000.0, 6000.1 and 6000.2 (none installed; latest patches
from the Hub).

### Feature parity on the compatibility path

| Feature | Compatibility path |
|---|---|
| Culling, LODs, clip boxes, light models, near fade, wide cones, intensity | Same (shared CPU code and shaders). |
| Forward, Forward+, Deferred | Yes. |
| Deferred+ | No (absent in URP 14; check whether 6.1–6.2 allow it in Compatibility Mode). |
| Light buffer (scale < 1, All / LargeOnly, both upsamplers) | Yes; scene depth through `_CameraDepthTexture` (costlier on tile GPUs). |
| Light buffer timing BeforeOpaques | Yes. Auto: yes where the URP 14 rules port, else after-opaques. |
| Depth input attachment | No (RenderGraph on 6000.6+ only). |
| Native render pass merging | Not controlled; URP 14 with Native RenderPass on is unverified (see risks). |

## Package, CI and docs

- `package.json`: `"unity": "2022.3"`, `"unityRelease": "20f1"` (or the tested patch, decision 1), and the URP version
  the minimum Editor resolves (as with 6000.3.0f1 today: install it and read the lock file). Burst 1.8.25,
  Collections 2.6.2 and Mathematics 1.3.3 stay. Unity 6 projects keep their bundled URP 17, because the dependency is
  only a minimum.
- Test projects: recreate `Unity-Tests/2022.3.62f3` (removed 2026-11-19) and add `Unity-Tests/6000.0.<latest>`, both
  with URP, MeshGen, the OpenUPM registry and Fuluma in `testables`. CI matrix: 2022.3.62f3, 6000.0.x, 6000.3.1f1,
  6000.6.1f1.
- `release.yml`: `INSTALLER_UNITY_VERSION` 2022.3.62f3, Installer project migrated down (as done for ShaderWorks).
- The dev project stays on 6000.7, where the compatibility path isn't compiled at all. Keep the 2022.3 test project
  openable locally (package by `file:` path), so the path gets compiled before every commit, not just in CI.
- Docs: README Requirements, Platform Support and Upcoming Changes; `Documentation~/FulumaRenderFeature.md` ("RenderGraph
  only"); CHANGELOG; the project's CLAUDE.md ("URP integration is RenderGraph-only", version gates, minimum).
- Version 0.2.0.

## Tests

- **Both paths in one Editor** on 6.0–6.2: run the render fixtures twice (`[TestFixture(RenderPath.RenderGraph)]`,
  `[TestFixture(RenderPath.Compatibility)]`), toggling `RenderGraphSettings.enableRenderCompatibilityMode` and
  restoring it (check it's settable from code). Add a parity test: the same scene through both paths, images equal
  within tolerance. That's the strongest check of the compatibility path.
- 2022.3: the whole suite on the compatibility path. Brute-force comparisons (`Render_MatchesBruteForceIntegral` and
  variants), light buffer, LargeOnly, timing and MSAA tests. Tests reading RenderGraph-only internals get
  path-neutral equivalents.
- Lifecycle on 2022.3: play-mode switches with domain reload off (the pre-6000.5 fallback runs there for real).
- Players on 2022.3: Windows D3D11/D3D12, Android Vulkan on the Mali-G52 and G77 phones. Measure the light buffer's
  fixed cost against the RenderGraph path on 6000.3. The profiling scripts in `Fuluma/Assets/{Profiling,Editor}` must
  compile on 2022.3, or profiling needs a separate 2022.3 project.

## Phases

1. **Groundwork, no behavior change:** partial split, generic draw loop, `UrpDepthPrepass` input struct, compile
   switches, `StoreFeatureLookup` through `SerializedObject`. The suite on 6000.3.0f1 and 6000.7 is unchanged.
2. **RenderGraph on 6.0–6.2:** API gates; suite on 6000.0.x in RenderGraph mode.
3. **Compatibility path, full resolution:** 2022.3 compiles and draws; `_DEPTH_NONE`; MSAA.
4. **Compatibility path, light buffer:** downsample, low-resolution draw, composite, LargeOnly.
5. **Compatibility path, early pass:** BeforeOpaques, then Auto.
6. **Verification:** test projects, CI matrix, dual-path fixtures, parity test, players, phone measurements.
7. **Release:** `package.json`, installer, docs, CHANGELOG, 0.2.0.

Rough size: like the clip-class work. Phases 3–5 are most of it; phase 6 needs Editors that aren't installed yet
(6000.0–6000.2, and optionally 2022.3.20f1).

## Risks and unknowns

- **Native RenderPass in URP 14:** a custom `Execute` that switches targets may break URP 14's native render pass
  merging, or fall back silently. Test with the renderer's Native RenderPass on, on a phone.
- **Several cameras of different sizes** (Scene view + Game view, split screen) reallocate the light buffer's RTHandles
  every camera if they're shared (decision 4).
- **Auto timing on URP 14:** porting the prepass rules may miss cases. Then the early pass silently doesn't run. The
  image stays correct and only the saving is lost, as on RenderGraph when another feature forces a prepass.
- **Untested compiled code:** on 6.0–6.2 the compatibility path compiles but most users never run it. Only the
  dual-path fixtures cover it.
- **Unknowns to settle first:** which 6.x minor added `TextureDesc.format`, `prepassLayerMask` and Deferred+; whether
  Deferred+ works in Compatibility Mode on 6.1–6.2; whether `enableRenderCompatibilityMode` is settable from test code;
  which URP version 2022.3.20f1 resolves; whether every shader variant compiles with URP 14's includes on Vulkan and
  Metal.

## Decisions (user's, 2026-11-25)

1. **Minimum: 2022.3.20f1** (`"unity": "2022.3"`, `"unityRelease": "20f1"`, Collections 2.6.2's floor), and with it
   6.0–6.2 on RenderGraph. 2022.3.20f1 is installed once to check that the package resolves and compiles; CI and the
   test project use 2022.3.62f3.
2. **Compatibility Mode on 6.0–6.2: supported.** Render tests run once per mode there, plus the parity test.
3. **6.3 with `URP_COMPATIBILITY_MODE`: not supported.** The guard is `!UNITY_6000_3_OR_NEWER` alone; 6.3+ is
   RenderGraph only.
4. **Light buffer targets on the compatibility path: one shared set**, reallocated when the camera size changes.
5. **Version 0.2.0.**
6. **Editors:** 6000.0 LTS only (6000.0.85f2, the newest the license covers; 6000.0.86f1 needs extended LTS). The
   6.0 gates are written for 6000.0; 6.1 and 6.2 compile the same gated code but aren't tested locally or in CI.
7. **Commits:** a local commit per phase, never pushed, in the repository's commit style.
8. **Players:** a git-ignored 2022.3 profiling project for desktop and phone measurements, only after the suite
   passes on 2022.3 and 6000.0.
