# Spot light pixel cost: ideas (2026-11-17, not implemented)

Written after the user ran 16384 lights at native 4K (RTX 3060), many near the camera: GPU-Z showed ~95% "GPU load"
and ~10% memory-controller load. Nothing below is implemented or measured yet unless it says so. The user asked for
ideas only; the next step was theirs to pick (see the end).

## Measured: Nsight GPU Trace, 2026-11-18

Profiling build (`Builds/Profiling`, SampleScene, 16384 static lights, start camera pose, light buffer scale 1,
**1920x1080** (not 4K: no resolution was passed and the player reused one Unity had saved; `gpu_trace.ps1` now passes
it explicitly, 4K by default), RTX 3060, D3D12, clocks locked to base ~1.32 GHz by Nsight so times are ~35% above normal play), metric set
"Async Compute Triage", frame 2 of 3 (`Builds/Profiling/Captures/*.ngfx-gputrace` + `BASE/*.xls`):

- GPU frame 9.60 ms, of which `Fuluma Draw SpotLightPass` 9.53 ms. Nothing else in the scene costs anything.
- **Shader-bound (instruction issue):** `sm__throughput` 77% (3.08 of 4 warp instructions per SM per cycle), pixel
  warps active 70% of slots, registers allocated 70%. Pipes: FMA 43%, ALU 40%, FMA-heavy 31%, XU (rcp, rsqrt, sin,
  cos, ...) 48%. Not memory: DRAM 4.9%, L2 5.6%, L1/tex 13.5% (mostly pixel-input reads). Not blending: CROP 21%,
  PROP 19.5%. Raster 3.7%. ZROP ~0.
- **Overdraw:** 134M samples into ZCULL per frame = **64.8 fragments per 1080p pixel** on average (the same view at 4K
  would be ~4x the fragments, ~540M); **none rejected** (this
  scene has no opaque geometry in front of the lights, so early-Z can't cull anything; the inside/outside pass split
  doesn't matter here).
- **Per fragment:** 1.12e9 warp instructions / (134M / 32) ≈ **270 instructions per fragment** on average (early-outs
  included), ~21 of them XU.
- Reading: time ≈ fragments × instructions, and the SM is issue-limited, so cutting either scales almost linearly.
  XU at 48% means transcendental throughput alone isn't the wall: the trig-free rule (item 9) trades atan2/tan for
  more rsqrt/rcp and would mostly move work between pipes on NVIDIA; total instruction count is what matters here.
- **4K trace** (same build, 3840x2160 exclusive fullscreen, base clocks, `Fuluma_2026_11_18_15_17_33.ngfx-gputrace`,
  per-draw timing on): GPU frame 75.3 ms, light pass 75.1 ms. 910M fragments = **110 per pixel**, none rejected;
  **~330 instructions per fragment** (~26 XU). SM throughput 84%, pixel warps active 79%, registers 78%; FMA 47%,
  ALU 45%, XU 52%; CROP 25%, PROP 23%, DRAM 7%. ~82 ps per fragment (1080p: ~71). Not directly comparable with the
  1080p trace: `translateLights` is on and 600 frames take much longer at 4K, so the lights had moved elsewhere
  (more of them near the camera). Freeze the lights for comparable traces.
  The shader profiler and warp stall reasons were **not** collected: those are per-architecture options and need
  `--architecture "Ampere GA10x"` (fixed in `gpu_trace.ps1 -PerDraw`, which now also uses "Top-Level Triage").
  Per-draw times are only inside the `.ngfx-gputrace` file (open it in Nsight Graphics); the CLI export has none.
- **Baseline for shader changes: 4K, frozen lights** (`gpu_trace.ps1 -PerDraw`, i.e. `-fulumaFreezeLights`, metric set
  "Top-Level Triage", real-time shader profiler on, per-draw timing; `Fuluma_2026_11_18_15_26_20.ngfx-gputrace`,
  236 MB): GPU frame **69.2 ms**, light pass **68.9 ms** at base clocks (~48 ms at boost, estimated).
  - 653M pixel-shader invocations = **79 per pixel**; 21.6M pixel warps, 94% lane use; **~400 instructions per
    invocation** (8.66e9 warp instructions / 21.6M warps), ~31 of them XU. ZCULL counts 846M samples (102 per
    pixel): it counts more than the shader runs, so the per-fragment figures of the two traces above (based on ZCULL)
    overstate fragments and understate instructions per fragment.
  - SM issue 84%; pixel warps active 79%; **warp launches stalled on register allocation 96% of the time** (occupancy
    is register-limited, yet ready warps are plentiful: "not selected" is the top stall reason).
  - Warp stall reasons: not selected 36%, wait (fixed-latency dependency) 11%, selected 8%, math pipe throttle 6%,
    dispatch 5%, MIO throttle 5% and short scoreboard 4% (both mostly XU/MUFU traffic), branch resolving 1.3%,
    L1/tex 0.8%. Reading: issue-saturated; fewer instructions is the lever; XU-related stalls are ~9%, so trig/rsqrt
    work matters some but isn't dominant. Average pixel warp latency 4476 cycles.
  - CROP 24%, PROP 23%, DRAM 7%, L2 4.6%: blending and memory have headroom.
  - Per-instruction hotspots (Top-Down Calls / shader source view) and per-draw times are only in the trace file:
    open it in Nsight Graphics (`F:/Portable/NVIDIA/host/windows-desktop-nomad-x64/ngfx-ui.exe`). Unity shaders carry
    no HLSL debug info, so hotspots show as DXIL/SASS.
- **After items 5-7** (2026-11-18, `Fuluma_2026_11_18_15_37_58.ngfx-gputrace`, same frozen scene: identical 653M
  invocations and 21.6M warps): light pass **59.1 ms (-14%)**, warp instructions 7.65e9 (-12%), **~353 per
  invocation**, XU 6.35e8 (-5%), SM issue 87%. DXBC cost model: miss path 116 -> 90, hidden-behind-depth path
  124 -> 98, longest path 568 -> 500 operations; vertex 85 -> 109 instructions (negligible). 1024² benchmarks (one
  run, ±0.1 ms): overlapping lights 2.08 -> 1.46 ms at full res, 0.69 -> 0.50 at scale 0.5; behind wall 0.28 -> 0.195.
  Render tests unchanged (brute force worst 0.3-1.8% per pixel).
- **Trig A/B (item 9)**, 2026-11-18, same frozen 4K scene (`Fuluma_2026_11_18_15_43_53.ngfx-gputrace`), switch
  `FULUMA_TRIG_FREE_QUADRATURE` (then in `FulumaSpotLightScattering.hlsl`): trig-free half-angle rule vs phi-GL3.
  **Decision: phi-GL3 kept**; the trig-free rule is archived in `Assets/Archive/SpotLightSampling` (README section
  "Trig-free quadrature") with the include as tested.
  - Light pass 57.9 / 58.7 / 57.9 ms vs 59.1 / 59.1 / 58.8: **~2% faster**. Warp instructions 7.40e9 vs 7.65e9
    (-3.3%), but XU instructions +5.6% (XU pipe 61% vs 57%; MIO throttle 8.0% vs 5.2%, short scoreboard 6.8% vs 5.1%),
    which eats part of the saving: on NVIDIA the extra rsqrt/rcp/sqrt queue on the slow XU pipe.
  - DXBC cost model: longest path 500 -> 473 (ALU 460 -> 430, SFU 40 -> 43).
  - Accuracy (render tests vs brute force): worst per pixel 0.7-1.8% vs 0.3-1.8%, mean 0.1-0.4% vs 0.1-0.4%; all
    tests pass. 1024² benchmarks: no difference within noise (1.51 vs 1.46 ms full res, 0.47 vs 0.50 at 0.5).
  - Not measured on mobile (Mali has hardware atan assist, so the balance may differ there).
- **Per-light resolution (item 1, `LightBufferLights.LargeOnly`)**, 2026-11-18, same frozen 4K scene, base clocks,
  `Builds/Profiling/compare_light_buffer.ps1` (results in `Captures/<run>/`), threshold 0.15:

  | Run | GPU frame | Light pass(es) | Downsample + composite |
  |---|---|---|---|
  | Full resolution | 59.3 ms | 59.1 | - |
  | Scale 0.5, All | 16.8 ms | 16.0 | 0.11 + 0.29 |
  | Scale 0.5, LargeOnly | 30.2 ms | 18.8 full-res (small) + 10.6 low-res (inside + large) | 0.11 + 0.29 |

  In this scene the lights under 0.15 of the screen height are about a third of the full-resolution cost (18.8 of
  59.1 ms) and the large/inside ones about two thirds (10.6 ms at a quarter of the pixels, ~40 ms at full resolution).
  LargeOnly halves the frame against full resolution while keeping small lights sharp, but costs ~1.8x the All mode.
  A lower threshold moves more lights to the buffer.
  - Threshold 0.05 (`-fulumaLargeLightScreenSize 0.05`): GPU frame 16.8 ms, the same as All. In this scene (radii
    4-6 in a 100x20x100 box, farthest ~70 units) every visible light is at least 0.05 of the screen height, so the
    full-resolution pass had nothing to draw (it doesn't even show in the trace) and the split cost nothing extra.
    The useful threshold for this scene is between 0.05 and 0.15; scenes with many small or distant lights will
    split differently.
  - Found on the way: `Hidden/Fuluma/LightBuffer` was stripped from player builds (nothing referenced it;
    `Shader.Find` returned null), so every player with a scale below 1 had silently drawn at full resolution. Fixed:
    the feature fills empty shader fields in the Editor (`AssignDefaultShaders`), and the renderer asset now references
    the shader.
- **VRS prototype (item 2)**, 2026-11-18, same frozen 4K scene, base clocks: `builder.SetShadingRateFragmentSize` on
  the full-resolution light pass (a temporary `FulumaSpotLightRenderPass.ExperimentalShadingRate` static and stress
  test switch `-fulumaShadingRate 2x2|4x4`, both removed after the measurement; supported on the RTX 3060 / D3D12, all
  sizes 1x1 to 4x4). To retry: `builder.SetShadingRateFragmentSize(size)` in `RecordFullResolutionPass`.

  | Run | GPU frame | SM | CROP (blending) | Raster |
  |---|---|---|---|---|
  | Full resolution | 59.3 ms | 87% | 29% | 4% |
  | VRS 2x2 | 22.5 ms | 66% | 74% | 10% |
  | VRS 4x4 | 22.5 ms | 33% | 74% | 10% |
  | Buffer, scale 0.5 | **16.8 ms** | 84% | 29% | 5% |

  VRS cuts shading as expected (SM 87 -> 66 -> 33%), but every pixel is still rasterized, depth-tested and blended
  into the full-resolution HDR target: blending becomes the limit at ~22.5 ms, so 4x4 gains nothing over 2x2. The
  buffer blends 4x fewer pixels into a smaller B10G11R11 target and wins by ~25%. The prediction from the
  full-resolution counters (floor ~17-20 ms) held. Verdict: **VRS doesn't pay off for this workload** (heavy additive
  overdraw); it could only matter where blending is cheap or overdraw low. On a tile-based GPU blending is on-chip,
  which might change the balance, but VRS support there is patchy (none through Metal).
- Not yet known: per-draw split (inside vs outside, per LOD) and per-instruction hotspots. Next trace: add
  `--time-every-action --real-time-shader-profiler` to `gpu_trace.ps1`. Also unknown: how many fragments miss the true
  cone (proxy waste); needs the debug-keyword heat map.
- ngfx CLI gotchas: never pass `--platform` (Qt takes it as its own plugin name and crashes: "no Qt platform plugin
  could be initialized"); GPU counters need "Allow access to the GPU performance counters to all users" in the NVIDIA
  Control Panel (per-adapter `RmProfilingAdminOnly` must be 0) or an elevated run.

## Diagnosis

- GPU-Z "load" is time busy, not unit utilization. 10% memory controller rules out DRAM bandwidth, not ROP blending
  or rasterization. Confirm with **Nsight Graphics GPU Trace** (SM vs CROP vs raster throughput) and its **Shader
  Profiler** (stalls per source line). RenderDoc gives per-draw times (inside vs outside pass, per LOD), a quad
  overdraw overlay, pixel history, shader debugging; not the hardware bottleneck. Mobile: Mali Offline Compiler
  (static cycles per variant), Arm Performance Studio, Snapdragon Profiler, Xcode GPU capture.
- Suspected main cost: **camera-inside lights** (inside pass, `ZTest Always`, back faces). Every one of their pixels
  has a real in-volume segment up to the scene depth, so no depth test can reject them; each can be a full-screen
  shader run (8.3M at 4K). Outside-pass lights already get early-Z.
- Cheap instrumentation to add: a debug keyword that outputs a constant per fragment (overdraw heat map), and one
  counting proxy fragments that miss the true cone (proxy waste).

## A. Shade fewer pixels (largest expected gains)

1. **Per-light resolution.** Culling already knows each light's screen size and pass. Send large and camera-inside
   lights to the downsampled light buffer, keep small ones at full resolution. Big near lights are the smoothest and
   most expensive. Reuses `DownsampledLightBuffer` and the depth-aware composite; needs the pass to draw two sets
   (low-res buffer, camera color) from the bucket split.
2. **Variable rate shading** for the light pass: `IRasterRenderGraphBuilder.SetShadingRateFragmentSize` exists in
   this URP (core package RenderGraph). 2x2 / 4x4 shading, depth test stays per pixel, no composite pass. The shader's
   scene-depth clip happens once per coarse block, so edges bleed about like bilinear at that scale. DX12 and Android
   Vulkan only, not Metal. Docs: https://docs.unity3d.com/Manual/urp/variable-rate-shading-implementation.html,
   demo https://github.com/Unity-Technologies/shading-rate-demo.
3. **Temporal accumulation**: checkerboard / jittered subset per frame + reprojection with motion vectors. 2-4x on
   the light pass (or a lower buffer scale at equal quality). Costs: ghosting with moving lights, history buffer,
   disocclusion handling.
4. **Froxel hybrid** (long term, research): large, near, overlapping lights into a frustum voxel grid (Wronski 2014,
   https://www.advances.realtimerendering.com/s2014/wronski/bwronski_volumetric_fog_siggraph2014.pdf; Hillaire 2015,
   https://www.slideshare.net/DICEStudio/physically-based-and-unified-volumetric-rendering-in-frostbite), thin beams
   stay analytic. Cost independent of overlap, but blurs thin shafts and ghosts.
- Rejected / weak: tiled compute accumulation (modest gain, still per-pixel x per-light integral, loses on-chip
  blending on TBDR); depth bounds test (per draw, not exposed in Unity); ReSTIR-style stochastic light selection
  (noise + denoiser for a term that has no visibility cost); sorting (no gain for additive).
- Mobile note (reasoning, not measured): the full-resolution path with the depth input attachment can merge into the
  opaque native render pass; the downsampled buffer forces a depth store/copy, a new pass and a full-screen composite,
  so on TBDR it may lose part of its desktop advantage. Measure on a device, check merging in the Render Graph Viewer.
- Already known: at 1024², light buffer scale 0.5 costs 0.69 ms vs 2.08 ms at full resolution.

## B. Cheaper pixels (guess: 20-40% combined)

Items 5-7 are **implemented** (2026-11-18); see "After items 5-7" above. What was done differs slightly from the
plan: the vertex shader passes camera minus light position, the view vector (instead of the world position), cosInner
and the two camera-inside flags; the cones' `c` coefficients and the sphere term stay per pixel (cheap from the passed
values); segment ends shared by two segments are computed once; `1 / H` is applied once per ray.

5. **Per-light constants in the vertex shader** (perspective only: the ray origin is the camera). Constant over a
   light's pixels: camera-to-light vector and its squared length, its dot with the axis, both cones' and the cap
   sphere's `c` coefficients, the camera-inside tests (`FulumaIsInsideCappedCone`), `cosInner` (now a division per
   pixel in `FulumaSpotCosInner`), `inverseRange2`, epsilon². Pass as flat varyings (watch varying count on mobile).
   Orthographic keeps the per-pixel path.
6. **Scalar sample math** in `FulumaSpotSampleValue`: instead of building `toPoint` (vec3) and dotting it, use
   `d² = h² + s²` and `axial = axialAtClosest + s · dot(rayDirection, axis)` with `s = H · tanNode` (~4 ops vs ~10).
7. **Outer cone first** in `RaySphereCappedConePair`: solve the outer cone + cap, check hit and scene depth, only then
   the inner cone. Missed proxy pixels skip a quadratic and its sqrt.
8. **Lookup tables: no.** Sun et al. 2005's F(u,v) airlight table exists for absorption; with none it collapses to
   the atan already used, and the spot falloff would add dimensions
   (https://cave.cs.columbia.edu/old/publications/pdfs/Sun_TOG05.pdf). NVIDIA's Fallout 4 volumetrics (GDC 2016)
   used 2D lookups for falloff powers 0 and 1 only; ours is a squared saturate. No evidence a LUT beats ALU today.
9. **Trig: two options to benchmark** (each segment now costs 1 `atan2` + 1 `tan`):
   - **Trig-free half-angle Gauss-Legendre** (derived by a research agent, checked in Python, no published source).
     For a segment [a, b] in the airlight angle φ, with A = a − tc, B = b − tc, H the epsilon-padded distance:
     - N = sqrt((H² + A²)(H² + B²)), c = H² + AB
     - tan h = H(B − A) / (c + N) if c ≥ 0, else (N − c) / (H(B − A))   (h = half width in φ, no cancellation)
     - q = tan h / (1 + sqrt(1 + tan² h))   (= tan(h/2), in [0, 1])
     - tan φm = (A ρa + B ρb) / (H (ρa + ρb)), ρ = rsqrt(H² + s²)   (midpoint, same as now)
     - per GL3 node u ∈ {−√0.6, 0, √0.6}: tan ψ = 2qu / (1 − q²u²), node s = H (tan φm + tan ψ) / (1 − tan φm tan ψ)
     - weight per node: GL weight × 2q / (1 + q²u²) (Jacobian of ψ = 2 atan(q u)), times 1/H as now.
     - Accuracy (1950 random rays, 30°/20° cone, ε 2%, total per ray): current φ-GL3 mean 0.24% / p99 2.4%,
       half-angle 0.30% / 2.8%, quarter-angle variant (one more sqrt) 0.25% / 2.5%. float32 same; worst node error
       0.75% of a 1e-4-long segment over 200k intervals, Jacobian error < 0.2%.
     - Cost: ~2 rsqrt, 2 sqrt, 4-5 rcp per segment vs atan2 (~15-25 ALU + rcp) + tan (sin, cos, rcp). On NVIDIA these
       special-function ops run at 1/8 the FMA rate on a separate unit, so it may be a wash there; may win on mobile.
   - **Polynomial atan + rational tan**: odd least-squares polynomial on [0,1] (3 terms 1.3e-3 rad, 4 terms 2e-4,
     5 terms 3e-5; true minimax a bit better) with range reduction; tan via a Padé rational. Drobot's `atanFast4`
     claims 7e-5 rad but measures 1.5e-3.
   - Don't go below degree 5: GL2 5.5% mean error, Simpson / Milne 7-8%; Boole 5-point 0.25%, GL4 0.01%. Product
     integration (polynomial fit × closed-form moments) cancels catastrophically in float32 like the old closed form.
   - Scripts: `docs/dev/Scripts/Quadrature/` — `q.py` (rule comparison), `q2.py` (φ-GL3 vs half-angle GL3 vs
     quarter-angle, needs `q.py` beside it), `q3.py` (float32 stress of the trig-free nodes), `t.py` (atan fits).
     Validate any shader change with `Render_MatchesBruteForceIntegral`, cost with `dxbc_cost.py`.
10. **Half precision on mobile** for the window and angular falloff (keep t, s, d² in float): ~2x for that math on
    Mali/Adreno, no change on desktop.
11. Small: CPU contribution culling (peak glow below a threshold); `discard` on pixels that miss the cone to skip the
    blend (only if Nsight shows CROP limits; ZWrite is off so early-Z survives); a cheaper shader per LOD draw for
    distant lights (little gain: small lights cover few pixels).

## Suggested order (offered to the user, not yet chosen)

1. Profile: Nsight GPU Trace + overdraw heat map; confirm the inside pass dominates.
2. Items 5-7 (low risk; check with the brute-force render test and `Benchmark_ManyOverlappingLights` /
   `Benchmark_LightsBehindWall`).
3. Benchmark the trig-free rule vs the current one (item 9).
4. Per-light resolution (item 1), then VRS (item 2).
5. Temporal accumulation (item 3) last.

## Measured on Android, 2026-11-18 (Mali System Metrics counters)

Devices: Xiaomi 15C (Mali-G52 MC2, 1600x720, 60 Hz) and Xiaomi 11T (Mali-G77 MC9, 2400x1080). Development IL2CPP
ARM64 player of SampleScene, Vulkan, frozen lights (`Builds/Profiling/android_matrix.sh`). Numbers are **GPU active
cycles per frame** (Mali `GPUActiveCy`): the GPU clock moves between ~0.8 and 1.1 GHz between runs and frame times snap
to vsync, so milliseconds don't compare. 60 fps at ~1 GHz is ~17 M cycles. The scene has no opaque geometry (no early-Z
rejection); thermal status stayed 0.

Mali-G52, light buffer off: 1 light 2.5 M (empty frame), 256 lights (108 drawn) 22.7 M, 512 51.3 M, 1024 (433 drawn)
76.4 M = 94 ms. The light shader is ALU bound (100 M ALU issue cycles over 2 cores, almost no texture or memory
traffic), about 10x overdraw. Mali-G77: 1024 lights 15.5 M.

Light buffer fixed cost (empty frame) was far larger than on desktop. Its parts, on the G52:
- Depth-aware composite ~4.1 M at any scale (5 depth conversions, 9 loads, 4 exp2 per pixel);
- downsample ~1.9 M more below scale 0.5 than at 0.5 (same number of loads, but long serial loops in few threads);
- URP's `_CameraDepthTexture` copy (whole-screen pass, +5.7 MB reads per frame) plus the composite's blend.

Changes, all kept (tests unchanged within tolerance, edge bleed still 0.00-0.02%):
1. Downsample gathers 2x2 texels per fetch (blocks at least 2 wide, i.e. scales up to 0.5; wider scales keep loads).
2. The R32F low-resolution depth holds **linear eye depth**: the composite converts one depth instead of five, and every
   low-resolution light fragment skips its conversion.
3. Depth-aware composite: one gather for the 4 depths; where all 4 are within 1/64 relative depth of the pixel, one
   hardware bilinear read (the weighted path runs only at depth edges).
4. The light buffer reads the camera's depth attachment directly when single-sampled; URP's depth copy is requested only
   with MSAA or for full-resolution draws without depth input attachments (`NeedsCameraDepthTexture`).
5. `real` (half on mobile) for the composite's weights and colors: no measurable change (the composite was no longer
   ALU bound after 3), kept as harmless.

| Mali-G52, M cycles/frame | before | after 1-2 | after 1-5 |
|---|---|---|---|
| empty, 0.25 depth-aware | 10.8 | 7.2 | 5.0 |
| empty, 0.25 bilinear | 6.7 | 4.8 | 4.2 |
| 1024 lights, 0.25 depth-aware | 17.5 | 13.9 | 11.7 |
| 1024 lights, 0.1 depth-aware | 12.7 | 9.0 | 6.7 |
| 1024 lights, 0.1 bilinear | 8.6 | 6.5 | 5.9 |

Mali-G77 after all changes: empty 1.1 (no buffer) / 1.8 (0.25, either upsample); 1024 lights 3.2 at 0.25, 2.4 at 0.1.

Light count sweep after the changes (depth-aware, ~42% of the stress box's lights on screen, M cycles/frame):

| lights (drawn) | G52 full | G52 0.25 | G52 0.1 | G77 full | G77 0.25 | G77 0.1 |
|---|---|---|---|---|---|---|
| 256 (108) | 22.9 | | | | | |
| 1024 (433) | 76.4 | 11.7 | 6.7 | 15.6 | 3.2 | 2.4 |
| 2048 (849) | | 19.1 | 8.6 | 31.9 | 4.5 | |
| 4096 (1759) | | 35.1 | 12.6 | | 7.6 | 3.7 |
| 8192 (3516) | | | 20.0 | | | 5.3 |

So at ~1 GHz, 60 fps on the G52 is about 1800 lights at 0.25 or ~7000 at 0.1 (full resolution ~180); 30 fps about
4000 at 0.25 (full resolution ~400). The G77 (2400x1080) draws about 1000 at full resolution in a 60 fps frame and
over 8000 through the buffer. LargeOnly lost on the G52 (small lights at full resolution cost more than the whole
buffer).

Half precision in the light shader itself (item 10 above) is **not** safe for the angular falloff:
`saturate(cos * angleScale - bias)` multiplies the cosine's error by `angleScale`, which reaches hundreds to thousands for
narrow cones, so fp16's ~5e-4 becomes tens of percent. Only the range window would fit fp16 (~3 of ~20 operations per
sample); not tried. Note `real` / `half` is float on desktop with the project's "platform default" precision model, so
the desktop tests can't catch fp16 artefacts: check such changes on a device.

### Why the buffer still costs on tile-based GPUs (native render passes)

Dumped RenderGraph's native render passes (reflection into `NativePassCompiler.contextData`; a temporary test, since
deleted). At full resolution URP merges `DrawOpaqueObjects`, the light pass (depth as input attachment) and
`DrawTransparentObjects` into **one** native pass, and the camera depth is never written to memory (store DontCare).
With the light buffer the frame becomes five passes: opaques (now **storing** depth and color), downsample (low-res),
low-res light draw, composite (loads and stores color), transparents (loads color and depth again). On the G52 that is
~9.5 MB writes and ~5.5 MB reads extra per frame (the 15C's 1600x720 depth alone is ~4.6-5.8 MB per store).

- The bilinear composite now binds the camera depth read-only, so RenderGraph merges it into the transparents pass
  (one color store + color/depth load less). No change measurable in the stress scene (black: AFBC and transaction
  elimination make storing black color nearly free); it should help in real content.
- The depth-aware composite reading depth as an **input attachment** (which allows the same merge) measured slower:
  +0.5 M cycles on the G52, +0.3 M on the G77. Reverted; it samples the depth attachment as a texture.
- What remains is inherent to drawing between opaques and transparents at another resolution: the opaque pass must
  store depth for the downsample. Ideas not tried: downsample from a depth prepass (URP "Force Prepass") so the light
  buffer is ready before opaques and the composite can join the opaque pass; or inject later
  (`AfterRenderingTransparents`) so only one split remains.

Remaining fixed cost after everything (empty frame, 0.25): G52 +2.5 M depth-aware / +1.7 M bilinear over the frame
without the buffer; G77 +0.7 / +0.6 M. Measurement note: the stress test's stats overlay now draws text on Android
(installed monospace font), which adds ~0.7 M cycles on the G52 to every run (baseline 2.5 -> 3.2 M).

### Light buffer before the opaques (depth prepass), 2026-11-18

Prototype (`FulumaRenderFeature.lightBufferBeforeOpaques`, `FulumaLightBufferEarlyPass`): a pass at
`AfterRenderingPrePasses` requests the depth texture, which makes URP render a depth prepass into
`_CameraDepthTexture`; the downsample and low-resolution draw run there, and only the composite runs after the opaques
(reading the prepass texture, camera depth attached read-only, so it can join URP's opaque native pass). The pictures
match the regular path exactly (`Render_LightBufferBeforeOpaques_MatchesTheBufferAfterOpaques`, 0.00%).

Measured with 300 occluder boxes (`-fulumaOccluders 300`, URP Simple Lit), M cycles/frame, regular -> before opaques:

| | G52 | G77 |
|---|---|---|
| empty, 0.25 depth-aware (no buffer: 6.9 / 2.3) | 7.9 -> 8.4 | 3.0 -> 3.1 |
| 1024 lights, 0.25 depth-aware | 10.9 -> 11.4 | 3.7 -> 3.7 |
| 1024 lights, 0.25 bilinear | 9.8 -> 9.7 | 3.5 -> 3.5 |
| 1024 lights, 0.1 depth-aware | 9.7 -> 10.2 | 3.8 -> 3.8 |
| 1024 lights, full resolution | 38.6 | 9.0 |

No gain: reads drop (G52 23.0 -> 16.5 MB/frame) but the prepass writes its own depth texture (+1.3 MB) and draws the
geometry again (+1700 primitives here; heavier scenes cost more). Also: with real geometry on screen the buffer's fixed
cost on the G52 is only ~+1.0 M (it was +2.5 M in the black empty scene, where AFBC made the normal frame's color store
nearly free, so the split looked expensive).

When something else already forces the prepass (simulated with the URP asset's Depth Texture on and the renderer's
depth texture mode Force Prepass; assets restored afterwards), the before-opaques path wins, by the cost of the split:

| M cycles/frame, regular -> before opaques | G52 | G77 |
|---|---|---|
| empty, 0.25 depth-aware (no buffer: 7.9 / 2.5) | 8.5 -> 8.4 | 3.2 -> 3.1 |
| 1024 lights, 0.25 depth-aware | 11.5 -> 11.3 | 3.9 -> 3.7 |
| 1024 lights, 0.25 bilinear | 10.4 -> 9.7 | 3.7 -> 3.5 |
| 1024 lights, 0.1 depth-aware | 10.4 -> 10.3 | 4.1 -> 3.9 |

G52 reads 23.3 -> 16.6 MB/frame. So the split costs ~2-7% of these frames; the full-resolution depth read for the
downsample is paid either way (from the opaque pass's stored depth or the prepass texture) and is small. Worth it only
with a prepass that exists anyway (Force Prepass, depth priming, SSAO / effects needing depth normals before opaques).

### Light buffer timing and scene depth (feature settings, 2026-11-18)

- `lightBufferTiming` Auto (default) / AfterOpaques / BeforeOpaques replaced the before-opaques bool. Auto draws the
  buffer before the opaques only when the renderer's settings make URP render a full depth prepass anyway (Depth
  Texture Mode "Force Prepass" with a depth texture, or depth priming; `UrpDepthPrepass` re-evaluates URP 17.7's rule
  from public settings). Prepasses other features cause (SSAO, depth normals) can't be seen without reflection into
  URP's pass queue, which the user rejected: BeforeOpaques is the setting for those projects.
- `useSceneDepth` (default on). Off: no depth reads (`_DEPTH_NONE`), no depth texture / prepass / copy requests, no
  depth attachment for the lights, the outside pass's depth test set to Always (`_FulumaOutsideZTest`; RenderGraph
  merges the pass into URP's opaque pass, whose depth stays bound), a light buffer without depth (bilinear). Measured,
  300 boxes, 1024 lights, M cycles/frame, depth on -> off:

| | G52 | G77 |
|---|---|---|
| empty, 0.25 (no buffer: 6.9 / 2.3) | 7.9 -> 6.0 | 3.0 -> 2.6 |
| full resolution | 38.7 -> 74.9 | 9.0 -> 15.4 |
| 0.25 | 10.9 -> 12.8 | 3.6 -> 3.8 |
| 0.1 | 9.7 -> 7.7 | 3.8 -> 3.0 |

  It removes the buffer's fixed cost but also the occlusion savings (the shader's early return for hidden volumes and
  the outside pass's early-Z): a win only where little occludes the lights.
- Found on the way: URP rebuilds a renderer mid-frame when its settings change (old feature disposed, then `Create`),
  and the new pass got a fresh, empty GPU mirror for that frame (no lights). `SpotLightGpuMirror.Release` now keeps
  a mirror released during a frame until `endContextRendering`, so a pass recreated in the same frame reuses it.

### Clip boxes: first GPU measurement (2026-11-18)

Stress scene, 1024 lights, 300 occluder boxes, `-fulumaClipRooms 12` (81 keep boxes, each light keeps its room;
`-fulumaClipCutOuts` adds a subtract box per room: 2 boxes per light, the general path). M cycles/frame:

| | G52 0.25 | G52 full | G77 0.25 | G77 full |
|---|---|---|---|---|
| no boxes | 10.9 | 38.5 | 3.6 | 9.0 |
| 1 box (fast path) | 12.6 | 61.8 | 3.7 | 10.5 |
| 2 boxes (general path: sort + walk) | 24.6 | 207.8 | 4.9 | 28.6 |
| 1 box, general path compiled out (experiment) | 11.4 | 42.4 | 3.6 | 9.5 |

The general path's code (10-value sorting network, 9-step interval walk with select-assigned pieces) raises the
variant's register use, which lowers occupancy for *every* pixel of the variant: the one-box fast path costs +60%
with it in the shader and +10% without (G52, full resolution). Accuracy is fine (brute-force tests within 1.2%).

### Clip boxes: three variants of the fix (2026-11-18)

Same scene and runs; three local branches from the snapshot `de4a522`:
1. `dev/clip-lighter-general`: the general path walks breakpoints (`FulumaClipNextBreakpoint` / `FulumaClipInside` on
   two `float4` of intervals and one `float4` of roles) and integrates each piece as it closes; one shader copy of the
   quadrature (lights without boxes go through the same walk).
2. `dev/clip-per-complexity`: culling's `SpotLightClipClassJob` orders every visible-light region by clip class (no
   box / one box / several); each class draws with its own material: plain, `_FULUMA_CLIP_BOX_SINGLE` (one interval
   or the ray around it, at most two pieces, no walk), `_FULUMA_CLIP_BOXES` (the old sorting network).
3. `dev/clip-both`: 2 with the walk of 1 as the general variant.

DXBC fragment (dcl_temps / longest path in the cost model): plain 10 / 500; single box 15 / 588; general: sorting
network 21 / 1425, walk 17 / 863 (19 / 1112 before keeping one quadrature copy).

M cycles/frame (rooms: one keep box per light; cut-outs: keep + subtract, the general variant):

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| no boxes (all branches) | 38.8 | 10.9 | 9.0 | 3.6 |
| 1: rooms / cut-outs | 54.3 / 62.6 | 12.5 / 13.3 | 11.1 / 12.3 | 3.8 / 3.9 |
| 2: rooms / cut-outs | 43.5 / 146.1 | 11.4 / 20.7 | 9.6 / 24.1 | 3.7 / 4.7 |
| 3: rooms / cut-outs | 43.1 / 62.8 | 11.4 / 13.3 | 9.5 / 12.3 | 3.7 / 3.9 |

Shrinking the walk's code (1262 → 737 DXBC instructions) changed nothing on the phones: the cost is per-pixel work
(box loads, slab tests, the walk), not code size. Per-class draws keep one-box lights at +11% (G52 full) instead of
+40%. Culling cost: the partition job is small (433 visible lights); main-thread scheduling is noisy on the G52 phone
(0.06-0.34 ms in every branch). Draw-pass CPU time is unchanged.

### Clip boxes: keep minus subtract class (2026-11-19)

Branch 3 plus a fourth class, exactly one keep and one subtract box (`_FULUMA_CLIP_BOX_PAIR`: the room's interval
minus the cut-out's, at most two pieces, no walk; the general walk keeps two keeps, two subtracts and 3–4 boxes). DXBC
fragment: 15 temps, longest path 641 (single box 588, general walk 863). Same scene and phones as above (the 11T is the
first unit); full-resolution runs pinned with `-fulumaLightBufferScale 1`, and `-fulumaClipRooms` now turns clip
boxes on whatever the renderer asset says (it had them off, scale 0.7, on this day). M cycles/frame:

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| no boxes | 38.8 | 10.9 | 9.1 | 3.6 |
| rooms (one box) | 42.9 | 11.5 | 9.6 | 3.6 |
| cut-outs (keep + subtract), general walk (branch 3) | 62.8 | 13.3 | 12.3 | 3.9 |
| cut-outs (keep + subtract), pair class | **47.2** | **11.9** | **10.4** | **3.7** |

Cut-outs went from +62% to +22% over no boxes on the G52 at full resolution (+35% to +14% on the G77). What remains is
mostly the second piece's integration where rays cross a cut-out.

### Clip boxes: two keeps in the pair class (2026-11-19)

The pair class now also takes two keep boxes (an L-shaped room: the union of both intervals, one piece when they
overlap) with a coherent per-light branch in `FulumaClipRayBoxPair`; two subtracts stay general (up to three pieces).
New stress scenario `-fulumaClipExtensions`: each room gets a keep "annex" half its size sticking out of its +x side,
so the lit region is larger than with rooms alone (compare L-rooms with L-rooms only). DXBC: 15 temps, longest path
656 (keep + subtract only: 641). Baseline = the committed `e8f908a` shader and job (two keeps through the general walk),
built with the same stress test. M cycles/frame:

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| cut-outs, committed (keep + subtract only) | 47.4 | 11.9 | 10.4 | 3.7 |
| cut-outs, with the two-keep branch | 49.7 | 12.0 | 10.5 | 3.7 |
| L-rooms, committed (general walk) | 68.6 | 13.8 | 13.4 | 3.9 |
| L-rooms, pair class | **55.3** | **12.5** | **11.6** | **3.8** |

Two keeps: −19% on the G52, −13% on the G77 at full resolution. The shared shader costs keep + subtract lights about
+5% on the G52 (rooms, unchanged code, moved +1%, so this is above noise) and nothing measurable on the G77.

### Clip boxes: two keeps as their own class, CPU cost (2026-11-19)

Split: keep + subtract (`_FULUMA_CLIP_BOX_PAIR`, `FulumaClipRayKeepMinusSubtract`) and two keeps
(`_FULUMA_CLIP_BOX_UNION`, `FulumaClipRayKeepUnion`) are separate classes, five in all (`int4` counts). CPU measured
from the stress test's stats (median of ~200 one-frame samples after warm-up, three runs each, stable to 0.01 ms), mixed
scene `-fulumaClipCutOuts -fulumaClipExtensions` (checkerboard: every light has two boxes, half keep + subtract, half two
keeps), full resolution:

| | G52 GPU M cycles | G52 culling schedule / job | G52 draw pass CPU | G77 GPU | G77 draw pass CPU |
|---|---|---|---|---|---|
| shared pair class (5 draws) | 51.0 | 0.084 / 0.092 ms | 0.38 ms | 10.7 | 0.19 ms |
| split (10 draws) | 50.2 | 0.084 / 0.091 ms | 0.55 ms | 10.6 | 0.28 ms |
| split, draws ordered by material | 49.3 | 0.086 / 0.091 ms | 0.55 ms | 10.6 | 0.28 ms |

Alone: cut-outs 47.3 (shared 49.7), L-rooms 51.7 (shared 55.3) on the G52. Culling (schedule, job, wait, upload) is
unchanged; the cost is per draw: about 35 µs on the G52 phone and 17 µs on the G77 phone for each extra instanced draw
(each binds its property block: transforms, lights, visible indices, boxes, references). Ordering draws by material
(class-major instead of region-major) didn't change CPU time; kept, since it never adds material switches.

### Clip boxes: box transforms in the vertex stage (2026-11-19)

The one-box, keep + subtract and two-keep variants no longer read boxes per pixel. The vertex stage loads each box and
passes the ray in box space as two terms per box (`FulumaClipBoxVaryings`): perspective = the camera in box space
(flat) and the box-space image of the view vector (interpolated; the pixel rescales it by 1 / |view vector|);
orthographic = the forward axis in box space (flat) and the box-space ray origin on the camera plane (interpolated,
affine in the vertex position). The fragment only runs `FulumaClipBoxSlab`. The general variant still loads per pixel.
DXBC fragment temps / longest path: one box 15 → 12 / 588 → 573, keep + subtract 15 → 12 / 641 → 609, two keeps
11 / 615; vertex 109 → 145 (one box) / 171 (two). Same-session A/B against the per-pixel build, M cycles/frame:

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| no boxes (reference) | 38.8 | 10.9 | 9.1 | 3.6 |
| rooms: per pixel → vertex | 42.9 → **40.7** | 11.4 → 11.2 | 9.6 → **9.1** | 3.6 → 3.6 |
| cut-outs | 47.2 → **42.1** | 11.9 → 11.5 | 10.4 → **9.4** | 3.7 → 3.6 |
| L-rooms | 51.9 → **47.6** | 12.3 → 11.9 | 11.2 → **10.4** | 3.7 → 3.7 |
| mixed | 49.1 → **44.4** | | 10.6 → **9.6** | |

Clip overhead over no boxes at full resolution on the G52: rooms +11% → +5%, cut-outs +22% → +9%. Draw-pass CPU and
culling unchanged (mixed: 0.55 / 0.28 ms). Orthographic brute-force tests added (`Render_ClipBoxesOrthographic_*`).

### Clip boxes: two subtracts as their own class (2026-11-19)

Sixth class `_FULUMA_CLIP_BOX_EXCLUDE` (`FulumaClipExcludeTwoPieces`: the ray minus both boxes, up to three pieces,
`FulumaSpotLightScatteringThreePieces`), boxes from the vertex stage like the other two-box classes. The general walk
now only sees three or four boxes. Counts per region are a `SpotLightClipClassCounts` struct; the grouping job is an
in-place counting sort (American flag sort) over the six classes. DXBC fragment: 12 temps, longest path 617 (general
walk 17 / 863). Stress scenario `-fulumaClipTwoCutOuts` (two pillar cut-outs per room, rooms not kept). Same-session
A/B against a build that sends two subtracts to the general walk, M cycles/frame:

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| two cut-outs, general walk | 71.7 | 14.0 | 13.8 | 3.9 |
| two cut-outs, own class | **52.5** | **12.4** | **11.2** | 3.8 |

### Varying budget (2026-11-19)

The limits, device data and counting rules are in the machine-wide guide `~/.claude/docs/shader-varying-limits.md`
(15 fragment-input rows on GLES and 64-component devices, one row per `TEXCOORD` plus one for `SV_Position` when the
fragment reads it). For this shader, which reads `SV_Position` for its depth load: the plain variant uses 5 user rows +
position; the two-box variants 10; the general variant with four unpacked boxes would have used 14 + 1 = 15, exactly the
floor, hence the packing below (12 + 1 = 13).

### Clip boxes: the general variant's four boxes in the vertex stage, packed (2026-11-19)

The general variant (three or four boxes) now also transforms its boxes in the vertex stage: all four slots'
flat and interpolated `float3` terms are packed into 3 + 3 `float4` rows (`FulumaPackClipBoxTerms`), the fragment
unpacks them, runs four slab tests and feeds the breakpoint walk (`FulumaClipIntervalsFromSlots`); no box loads remain
in any variant. GLES fragment inputs: locations 0–11 (12 rows) + `gl_FragCoord` = 13 of the 15 guaranteed (verified in
the GLES dump: HLSLcc maps TEXCOORDn to `location = n`, one row each). DXBC: fragment 17 temps, longest path 863 → 829;
vertex 228 instructions. Stress scenario `-fulumaClipCutOuts -fulumaClipTwoCutOuts` (room kept, two pillars cut out:
three boxes per light). Same-session A/B against the per-pixel general variant, M cycles/frame:

| | G52 full | G52 0.25 | G77 full | G77 0.25 |
|---|---|---|---|---|
| three boxes, per pixel | 71.7 | 14.2 | 13.6 | 4.0 |
| three boxes, vertex stage (packed) | **64.5** | **13.5** | **13.2** | **3.9** |

### VRS on these devices

Not testable: neither GPU exposes `VK_KHR_fragment_shading_rate` (what Unity's VRS uses). The G52 (Bifrost) has no
shading-rate support; the G77 has only `VK_EXT_fragment_density_map` (Unity uses it for XR foveation only). Per-draw VRS
on Mali starts with the 4th-generation Valhall GPUs (G710 and later).

Open lead: the draw pass CPU time was ~3 ms at 0.25 with 2048+ lights on the 15C (0.1-0.3 ms otherwise), not
investigated.

### CPU cost per instanced draw: what the property block costs (experiment, 2026-11-19)

Temporary player switch (removed afterwards) with three draw-loop variants, same frozen scenes, full resolution, median
of ~200 one-frame samples per run, two runs each, builds identical otherwise. Variant 2 rendered pixel-identical to
variant 0 (outside the stats overlay). "Draw pass CPU" in ms:

| Variant | G52 rooms (5 draws) | G52 mixed (10 draws) | G77 rooms | G77 mixed |
|---|---|---|---|---|
| 0: today (full block per draw: 7 buffers, ints, texture, offset, proxy shape) | 0.38 | 0.55 | 0.19 | 0.28 |
| 1: proxy shape set once, not per draw | 0.39 | 0.55 | 0.19 | 0.28 |
| 2: per-pass bindings on the materials, per-draw block = offset + shape | **0.25** | **0.33** | **0.12** | **0.17** |

Per extra draw: 32 → 16 µs (G52 phone), 17 → 8.6 µs (G77). The fixed part drops too (~0.22 → 0.17 ms on the G52).
So the cost is the block's size (Unity copies the whole block into every recorded draw and binds it again), not
per-draw `Set*` calls: variant 1 changed nothing. Culling, uploads and GPU cycles were unchanged.

What a real implementation needs (variant 2 cut corners): set the materials' buffers when the camera is recorded, not
during execution (command buffers read material state when they run); the dynamic pool needs its own material set
(or the pools merge into one stream with a pool bit in the visible index); the low-resolution materials get their own
bindings; per-camera state on shared materials relies on URP executing each camera before recording the next (already
relied on for keywords).

Implemented (same day): per pool and clip class one material (`_staticMaterials`, `_dynamicMaterials` and their
low-resolution sets; the static plain material is the settings holder itself, the dynamic set exists only once the
dynamic pool has lights), bound once per camera at recording (`BindMaterials`: pool buffers, clip boxes, MSAA count);
copies re-sync from their holder only when it changed (`_holderVersion`; copying every camera cost 0.08 ms on the G52
phone). The per-draw block keeps the visible-index buffer (it can be recreated at upload, after recording), the offset,
the proxy shape and the low-resolution depth. Draw pass + material binding, ms:

| | G52 rooms (5 draws) | G52 mixed (10 draws) | G77 rooms | G77 mixed |
|---|---|---|---|---|
| before | 0.38 | 0.55 | 0.19 | 0.28 |
| after (draw + binding) | **0.30 + 0.03** | **0.41 + 0.03** | **0.15 + 0.02** | **0.20 + 0.02** |

Per extra draw 32 → 22 µs (G52 phone), 17 → 11 µs (G77). GPU cycles unchanged; dynamic lights the same. Material
memory: ~3–5 KB native each, up to 24 + 1 per renderer (`~/.claude/docs/unity-material-memory.md`). Found on the way:
a dynamic light added after a frame's first render is drawn from the next frame on (the dynamic rings are written once
per `Time.frameCount`); tests reset that guard per render (`SpotLightGpuMirror.ForgetFrameGuardsForTests`).

### Wide cones: cancellation-free intersection variant (2026-11-20)

Half-angles past ~80 degrees (up to the 89.9 clamp) showed holes and speckles near the cone axis. Cause: the cone
quadratic's discriminant `b^2 - 4ac` is a tiny difference of two terms of size (distance to the apex)^2, so float32
rounding swamps it for rays near the axis of a wide cone (float32 replica: 6x off at 85 degrees from 40 units; Editor:
exact to 175 degrees full angle at 4-11 units, holes at 179.8). Fixed in an opt-in shader variant
`_FULUMA_WIDE_CONES` (feature setting `Wide Cone Precision`, default off; the user picks the cost):
`disc/4 = cos^2 (|m|^2 sin^2 - dot(m, axis)^2)` with `m = rayToApex x rayDirection`.
Accuracy with it on: within 2% of brute force at 170 and 179.8 degrees in every view
(`Render_WideConePrecision_MatchesBruteForceIntegral`, all four rendering paths). Cost:

| | Off | On | Change |
|---|---|---|---|
| RTX 3060, 64 overlapping 60-degree lights, 1024^2 | 1.797 ms | 1.869 ms | +4.0% |
| RTX 3060, same with 170-degree lights | 5.114 ms | 5.349 ms | +4.6% |
| Mali-G52 (15C), stress scene 1024 lights, full res, GPU cycles | 78.0 / 78.2 M | 81.9 / 84.0 M | +5-7% |
| Mali-G77 (11T), same | 15.7 / 15.7 M | 16.0 / 16.1 M | +2% |

## Feature re-measurement on both phones (2026-11-23)

Vulkan, stress scene (1024 frozen lights, 300 occluders), feature defaults pinned on the command line (Isotropic,
Range Scaled, no glare, event After Rendering Skybox, Depth Aware, All, Auto timing), 2 interleaved rounds of 22 s, GPU
active cycles of the last 3 s. Prefixes: f = full resolution, h = 0.5 buffer, q = 0.25, t = 0.1. Driver:
`Fuluma/Builds/Profiling/feature_matrix.sh` + `feature_summary.py` (git-ignored); every Fuluma variant kept in the build.
Thermal status 0 throughout. GLES 3.x couldn't run: Mali's drivers expose no vertex SSBOs (`docs/dev/Backlog.md`).

MSAA off (`-fulumaMsaa 1`), the README's numbers:

```
25078RA3EE vk: GPU active Mcycles per frame (mean of rounds), change vs the resolution's baseline
config          Mcycles  vs base  spread  cull job  schedule  drawn  thermal
f_base             39.0    +0.0%    0.5%     0.072     0.086    433        0
f_cut              42.5    +9.0%    0.1%     0.091     0.108    433        0
f_hg               44.6   +14.3%    0.0%     0.079     0.071    433        0
f_keeps            47.8   +22.4%    1.3%     0.081     0.079    433        0
f_nodepth          76.7   +96.6%    0.1%     0.065     0.065    433        0
f_nofade           39.1    +0.1%    0.4%     0.068     0.139    433        0
f_nolods           39.9    +2.1%    0.4%     0.062     0.072    433        0
f_opaques          39.2    +0.4%    0.7%     0.081     0.143    433        0
f_point            39.0    +0.0%    0.1%     0.091     0.068    433        0
f_rooms            41.3    +5.7%    0.4%     0.070     0.077    433        0
f_three            66.2   +69.6%    0.4%     0.108     0.154    433        0
f_twocut           53.2   +36.4%    1.9%     0.086     0.078    433        0
f_unity            40.2    +3.1%    0.3%     0.093     0.076    433        0
f_wide             40.7    +4.3%    0.7%     0.146     0.118    433        0
h_base             17.0    +0.0%    0.1%     0.072     0.204    433        0
h_before           16.9    -0.5%    0.1%     0.060     0.073    433        0
h_bilinear         16.1    -5.1%    0.0%     0.081     0.108    433        0
h_cut              18.1    +6.5%    0.3%     0.071     0.076    433        0
h_hg               18.8   +10.5%    0.1%     0.081     0.069    433        0
h_keeps            19.7   +15.7%    0.4%     0.069     0.126    433        0
h_large            21.3   +25.2%    0.5%     0.069     0.133    433        0
h_nodepth          26.8   +57.7%    0.0%     0.081     0.075    433        0
h_nofade           17.0    -0.0%    0.0%     0.087     0.100    433        0
h_nolods           17.4    +2.1%    0.3%     0.046     0.090    433        0
h_opaques          17.1    +0.2%    0.2%     0.080     0.250    433        0
h_point            17.1    +0.4%    0.3%     0.087     0.126    433        0
h_rooms            17.3    +1.7%    0.5%     0.116     0.106    433        0
h_three            24.5   +44.0%    0.2%     0.080     0.170    433        0
h_twocut           21.1   +23.8%    0.2%     0.060     0.104    433        0
h_unity            17.4    +2.5%    0.5%     0.103     0.074    433        0
h_wide             17.5    +3.1%    0.2%     0.105     0.114    433        0
q_base             10.9    +0.0%    0.7%     0.060     0.180    433        0
q_nodepth          13.0   +18.8%    0.2%     0.066     0.069    433        0
t_base              9.6    +0.0%    0.2%     0.064     0.123    433        0
t_nodepth           7.7   -19.6%    0.7%     0.047     0.171    433        0

21081111RG vk: GPU active Mcycles per frame (mean of rounds), change vs the resolution's baseline
config          Mcycles  vs base  spread  cull job  schedule  drawn  thermal
f_base              9.0    +0.0%    1.1%     0.052     0.078    433        0
f_cut               9.3    +3.4%    0.1%     0.061     0.075    433        0
f_hg                9.7    +7.4%    0.2%     0.056     0.056    433        0
f_keeps            10.3   +14.0%    0.1%     0.059     0.059    433        0
f_nodepth          15.6   +73.7%    0.4%     0.050     0.056    433        0
f_nofade            8.9    -0.9%    0.1%     0.053     0.057    433        0
f_nolods            9.0    +0.1%    0.3%     0.050     0.065    433        0
f_opaques           8.9    -0.7%    0.3%     0.052     0.055    433        0
f_point             8.9    -0.8%    0.1%     0.052     0.070    433        0
f_rooms             9.0    -0.3%    0.2%     0.057     0.058    433        0
f_three            13.2   +46.7%    0.5%     0.056     0.068    433        0
f_twocut           11.1   +23.6%    0.2%     0.051     0.058    433        0
f_unity             8.9    -1.4%    0.6%     0.052     0.057    433        0
f_wide              9.1    +0.9%    0.1%     0.049     0.049    433        0
h_base              4.7    +0.0%    0.4%     0.053     0.051    433        0
h_before            4.7    +1.3%    0.4%     0.054     0.112    433        0
h_bilinear          4.5    -3.1%    0.6%     0.062     0.057    433        0
h_cut               4.7    +1.0%    0.0%     0.066     0.080    433        0
h_hg                4.9    +4.2%    0.5%     0.053     0.071    433        0
h_keeps             4.9    +5.9%    0.1%     0.050     0.057    433        0
h_large             5.4   +16.1%    0.1%     0.052     0.057    433        0
h_nodepth           6.2   +33.0%    0.3%     0.071     0.081    433        0
h_nofade            4.6    -0.3%    0.2%     0.053     0.076    433        0
h_nolods            4.7    +0.8%    0.1%     0.044     0.056    433        0
h_opaques           4.6    -0.2%    0.4%     0.056     0.084    433        0
h_point             4.6    -0.2%    0.3%     0.050     0.068    433        0
h_rooms             4.6    -0.8%    0.3%     0.067     0.060    433        0
h_three             5.7   +22.2%    0.3%     0.050     0.097    433        0
h_twocut            5.2   +11.2%    0.5%     0.056     0.081    433        0
h_unity             4.6    -0.3%    0.2%     0.057     0.066    433        0
h_wide              4.7    +0.5%    0.2%     0.052     0.063    433        0
q_base              3.5    +0.0%    0.1%     0.056     0.084    433        0
q_nodepth           3.7    +6.4%    0.6%     0.051     0.072    433        0
t_base              3.6    +0.0%    1.3%     0.052     0.080    433        0
t_nodepth           2.8   -21.4%    0.2%     0.060     0.059    433        0
```

With the dev URP asset's MSAA 4x (the light buffer then reads URP's depth copy instead of the camera's depth
attachment), the buffer's fixed cost dominates: base Mcycles full / 0.5 / 0.25 / 0.1 = 46.2 / 32.1 / 26.2 / 24.6 (G52)
and 9.7 / 14.0 / 12.7 / 13.1 (G77; downsampling costs more than full resolution there). Before Opaques timing at 0.5
then saves 36% (G52) and 64% (G77). Full tables: `feature_summary.py <model> vkmsaa4`.

## Desktop re-measurement, RTX 3060 (2026-11-23)

4K fullscreen, 16,384 lights, 300 occluders, MSAA off, same configurations and pinned defaults as the phone matrix,
GPU frame time from FrameTimingManager (normal clocks), 4 interleaved rounds of 24 s (`desktop_matrix.sh`,
`desktop_summary.py`). Runs are steady within 0.5% but whole runs land up to ~5% apart (boost clock per launch).

```
rtx3060: GPU frame ms (mean of rounds), change vs the resolution's baseline
config           gpu ms  vs base  spread  cull job  draw cpu  drawn
f_base            21.91    +0.0%    5.4%     0.369     0.121   6321
f_cut             24.44   +11.5%    0.4%     0.373     0.113   6321
f_hg              24.09   +10.0%    1.3%     0.441     0.112   6321
f_keeps           27.44   +25.2%    5.3%     0.390     0.124   6321
f_nodepth         41.59   +89.8%    6.1%     0.391     0.118   6321
f_nofade          21.71    -0.9%    1.2%     0.345     0.108   6321
f_nolods          21.85    -0.3%    3.0%     0.336     0.101   6321
f_opaques         22.02    +0.5%    6.5%     0.357     0.113   6321
f_point           21.98    +0.3%    4.6%     0.339     0.110   6321
f_rooms           23.12    +5.5%    0.9%     0.376     0.124   6321
f_three           39.70   +81.2%    2.5%     0.441     0.117   6321
f_twocut          30.08   +37.3%    0.4%     0.386     0.104   6321
f_unity           22.63    +3.3%    5.4%     0.356     0.106   6321
f_wide            22.59    +3.1%    1.0%     0.390     0.106   6321
h_base             6.47    +0.0%    4.0%     0.302     0.058   6321
h_before           6.54    +1.1%    3.4%     0.312     0.061   6321
h_bilinear         6.45    -0.2%    3.4%     0.319     0.058   6321
h_cut              7.34   +13.5%    7.3%     0.318     0.057   6321
h_hg               7.06    +9.2%    3.4%     0.311     0.058   6321
h_keeps            7.86   +21.6%    2.7%     0.326     0.065   6321
h_large            7.46   +15.3%    3.1%     0.325     0.082   6321
h_nodepth         11.65   +80.1%    1.1%     0.326     0.121   6321
h_nofade           6.46    -0.1%    3.7%     0.309     0.054   6321
h_nolods           6.51    +0.6%    4.1%     0.319     0.049   6321
h_opaques          6.55    +1.3%    8.8%     0.311     0.057   6321
h_point            6.49    +0.3%    3.4%     0.319     0.057   6321
h_rooms            6.96    +7.6%    6.6%     0.324     0.059   6321
h_three           11.27   +74.3%    0.9%     0.324     0.058   6321
h_twocut           8.66   +33.9%    2.3%     0.314     0.060   6321
h_unity            6.63    +2.5%    3.5%     0.325     0.056   6321
h_wide             6.63    +2.6%    3.8%     0.310     0.056   6321
q_base             2.51    +0.0%    4.4%     0.341     0.042   6321
q_nodepth          4.05   +61.3%    5.3%     0.314     0.068   6321
t_base             2.24    +0.0%    6.2%     0.325     0.039   6321
t_nodepth          2.22    -0.7%   19.2%     0.330     0.067   6321
```

Nsight GPU Trace under the 2026-11-18 conditions (no occluders, event After Opaques, base clocks), Captures
`*_2026-11-23`: full 59.26 ms (was 59.31), 0.5 All 16.71 (16.81), 0.5 Large Only 29.98 (30.17). Full resolution: SM
throughput 87.4% (86.9), instructions +0.8%, texture wavefronts doubled but L1 tex hit rate 53 -> 99%, FE wait-for-idle
stalls 15 -> 5% (13 draws instead of 15), async copy engine reported active the whole frame (unexplained), memory
clocks ~10% lower during the trace.

## Two more phones (2026-11-24): Honor 8X (Kirin 710, Mali-G51), Poco X3 (Snapdragon 732G, Adreno 618)

Same Vulkan matrix as the 15C/11T (2 rounds, MSAA off). The Honor reports Mali cycles; the Poco has no Mali counters,
so its column is GPU ms from FrameTimingManager (noisier, ±5%). Notable: on Adreno the general clip box variant (three
boxes) costs +175% at full resolution and +118% at 0.5 (Mali +39..70%): worth a look with the Adreno Offline Compiler
(register pressure or the packed box varyings?). Before Opaques at 0.5 costs +13% on the Poco (no prepass in the scene).

```
JSN-L21 vk: GPU active Mcycles per frame (or GPU ms without Mali counters), mean of rounds, change vs the resolution's baseline
config          Mcycles  vs base  spread  cull job  schedule  drawn  thermal
f_base            101.1    +0.0%    0.4%     0.086     0.079    415        0
f_cut             107.0    +5.8%    0.6%     0.105     0.096    415        0
f_hg              113.0   +11.7%    2.8%     0.115     0.113    415        0
f_keeps           120.1   +18.7%    2.4%     0.115     0.107    415        0
f_nodepth         189.9   +87.7%    0.3%     0.114     0.087    415        0
f_nofade          100.6    -0.5%    0.3%     0.126     0.097    415        0
f_nolods          102.9    +1.7%    2.9%     0.089     0.092    415        0
f_opaques         100.8    -0.4%    0.2%     0.115     0.093    415        0
f_point           101.0    -0.1%    0.1%     0.116     0.093    415        0
f_rooms           100.0    -1.2%    3.3%     0.113     0.096    415        0
f_three           171.5   +69.5%    4.4%     0.113     0.130    415        0
f_twocut          131.7   +30.2%    4.2%     0.115     0.100    415        0
f_unity           104.3    +3.1%    0.7%     0.126     0.096    415        0
f_wide            102.5    +1.4%    2.5%     0.114     0.093    415        0
h_base             44.2    +0.0%    0.2%     0.114     0.102    415        0
h_before           44.5    +0.6%    1.9%     0.114     0.085    415        0
h_bilinear         42.6    -3.7%    0.3%     0.116     0.095    415        0
h_cut              45.8    +3.6%    1.0%     0.114     0.103    415        0
h_hg               47.6    +7.5%    0.9%     0.114     0.095    415        0
h_keeps            49.4   +11.7%    0.2%     0.112     0.101    415        0
h_large            49.6   +12.1%    0.6%     0.115     0.088    415        0
h_nodepth          66.9   +51.2%    2.9%     0.137     0.088    415        0
h_nofade           43.9    -0.8%    0.6%     0.084     0.213    415        0
h_nolods           44.1    -0.4%    1.1%     0.086     0.100    415        0
h_opaques          43.8    -1.0%    1.8%     0.094     0.080    415        0
h_point            44.3    +0.1%    0.6%     0.114     0.084    415        0
h_rooms            43.7    -1.2%    0.0%     0.114     0.090    415        0
h_three            61.4   +38.7%    1.0%     0.145     0.122    415        0
h_twocut           53.3   +20.5%    0.8%     0.112     0.092    415        0
h_unity            44.7    +1.1%    0.8%     0.113     0.083    415        0
h_wide             44.7    +1.1%    0.2%     0.113     0.085    415        0
q_base             28.0    +0.0%    0.8%     0.115     0.084    415        0
q_nodepth          31.9   +13.9%    5.0%     0.107     0.095    415        0
t_base             26.3    +0.0%    4.5%     0.095     0.114    415        0
t_nodepth          20.5   -22.3%    0.6%     0.124     0.095    415        0

M2007J20CG vk: GPU active Mcycles per frame (or GPU ms without Mali counters), mean of rounds, change vs the resolution's baseline
config          Mcycles  vs base  spread  cull job  schedule  drawn  thermal
f_base             33.2    +0.0%    1.4%     0.059     0.223    433        0
f_cut              36.8   +10.6%    1.8%     0.067     0.325    433        0
f_hg               34.5    +3.7%    0.0%     0.077     0.227    433        0
f_keeps            38.2   +15.0%    3.0%     0.084     0.245    433        0
f_nodepth          46.7   +40.4%    3.2%     0.068     0.230    433        0
f_nofade           34.1    +2.5%    1.1%     0.082     0.192    433        0
f_nolods           32.7    -1.8%    3.9%     0.057     0.135    433        0
f_opaques          33.9    +1.8%    2.7%     0.075     0.272    433        0
f_point            32.2    -3.1%    5.4%     0.077     0.277    433        0
f_rooms            32.5    -2.2%    0.1%     0.082     0.223    433        0
f_three            91.4  +175.0%    4.0%     0.082     0.317    433        0
f_twocut           42.3   +27.1%    5.7%     0.080     0.225    433        0
f_unity            32.7    -1.6%    0.2%     0.082     0.090    433        0
f_wide             33.8    +1.6%    2.6%     0.077     0.183    433        0
h_base             20.6    +0.0%    2.3%     0.083     0.276    433        0
h_before           23.2   +12.7%    0.8%     0.058     0.145    433        0
h_bilinear         20.5    -0.8%    7.3%     0.083     0.231    433        0
h_cut              22.2    +7.8%    0.1%     0.079     0.201    433        0
h_hg               21.0    +1.7%    0.8%     0.082     0.170    433        0
h_keeps            22.6    +9.8%    1.5%     0.080     0.314    433        0
h_large            23.2   +12.4%    2.5%     0.068     0.159    433        0
h_nodepth          26.3   +27.6%    0.9%     0.060     0.218    433        0
h_nofade           20.6    -0.0%    2.5%     0.067     0.172    433        0
h_nolods           20.7    +0.2%    1.8%     0.059     0.233    433        0
h_opaques          20.6    -0.2%    1.5%     0.092     0.291    433        0
h_point            20.1    -2.5%    1.6%     0.083     0.239    433        0
h_rooms            20.7    +0.2%    4.6%     0.082     0.235    433        0
h_three            45.0  +118.4%    5.7%     0.074     0.207    433        0
h_twocut           23.9   +16.0%    2.0%     0.079     0.207    433        0
h_unity            20.7    +0.3%    0.7%     0.075     0.154    433        0
h_wide             20.0    -3.2%    3.3%     0.081     0.146    433        0
q_base             16.8    +0.0%    1.8%     0.066     0.167    433        0
q_nodepth          16.8    +0.3%    5.9%     0.073     0.121    433        0
t_base             15.1    +0.0%    1.6%     0.075     0.185    433        0
t_nodepth          13.9    -8.4%    2.2%     0.080     0.214    433        0
```
