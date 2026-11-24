# Inspecting compiled shaders (Unity 6000.7)

How to get the generated assembly / intermediate representation of the spot light shader
(`Hidden/Fuluma/Analytical/SpotLight`) for every graphics API, read it, and measure it.

- **Verified** means it was run on this project: Unity 6000.7.0b4, URP 17.7, Windows, RTX 3060, D3D12.
- **Documented** means it comes from Unity or vendor documentation (sources at the end), not run here.
- **Unverified** is marked where neither applies.

Unity's manual moved: `docs.unity3d.com/6000.x` links now redirect to `docs.unity.com/en-us/engine/6000.x/...`.

## 1. Which tool for which question

| Question | Tool | Section |
|---|---|---|
| What does Unity hand to the compiler (includes and `#if`s resolved)? | Inspector *Preprocess only*, or `PreprocessVariant` | 2, 3 |
| What bytecode / IR does each API get? | Inspector *Compile and show code*, or `CompileVariant` + disassembler | 2, 3, 4 |
| Did a shader change make it cheaper (static)? | DXBC instruction counts | 4 |
| Cycles, registers, bottleneck on a specific mobile/desktop GPU? | Mali Offline Compiler, Adreno Offline Compiler, Radeon GPU Analyzer | 5 |
| What happens in a real frame, per pixel? | RenderDoc, PIX, Xcode Metal debugger | 6 |
| Roughly how many cycles per pixel / vertex? | `docs/dev/Scripts/dxbc_cost.py` (a model, not vendor output) | 4, 10 |
| Is it faster on the GPU? | `Benchmark_ManyOverlappingLights`, `Benchmark_LightsBehindWall` | 8 |

## 2. In the editor: the Shader Inspector (documented)

Select the shader asset. In the Inspector's **Imported object** table:

- The **Compiled code** row has **Compile and show code** with a dropdown.
  - Targets: **Current graphics device**, **Current build platform**, **All platforms**, or **Custom** (pick APIs). The
    manual lists the output per API: D3D → DXBC, GLES3x → GLSL, Metal → MSL, OpenGLCore → GLSL, Vulkan → SPIR-V,
    WebGPU → WGSL.
  - **Skip unused shader_features** strips variants that no material uses. The dropdown shows how many variants
    will be compiled, and **Show** lists them. It compiles the whole (filtered) variant set, not one chosen variant.
- **Preprocess only** outputs preprocessed source instead of compiled code. **Strip #line directives** is available
  only together with it.
- Unity 6.3 changed compute shaders' compile button to compile for all APIs, including ones not enabled in Player
  Settings. Limit it with `#pragma only_renderers` / `exclude_renderers` (`d3d11` also covers D3D12).
- **Known issue:** both buttons did nothing for shaders inside the `Packages` folder. In this repo the package is under
  `Assets/root`, so the buttons are fine here; in a project that installs Fuluma as a package they may not work. Use
  section 3 there. Not checked for 6000.7.
- **Unverified for Unity 6:** the result opening as `Temp/Compiled-<ShaderName>.shader`, and per-program `// Stats:`
  instruction-count comments in it. Both are from older versions. The D3D disassembly's own
  `// Approximately N instruction slots used` line can't be relied on either: fxc printed 0 for this shader (section 4).

## 3. From code: `ShaderData.Pass.CompileVariant` (verified)

The editor API compiles one variant of one pass for any platform, without building a player:

```csharp
var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
var info = pass.CompileVariant(ShaderType.Fragment, keywords, ShaderCompilerPlatform.D3D,
    BuildTarget.StandaloneWindows64, forExternalTool: true);
// info.Success, info.ShaderData (byte[]), info.Messages (ShaderMessage: severity, message, file, line),
// info.ConstantBuffers, info.ResourceBindings, info.TextureBindings, info.Attributes
```

- `forExternalTool: true` returns plain bytecode for third-party tools instead of Unity's runtime container.
- Optional overload arguments: `BuiltinShaderDefine[] platformKeywords` and `GraphicsTier`. Without them Unity uses the
  platform defaults for the build target and the project's settings. This project is in **Gamma** color space
  (`m_ActiveColorSpace: 0`), so `UNITY_COLORSPACE_GAMMA` is defined and the sRGB-to-linear conversion compiles out.
- On OpenGLCore, GLES3x, Switch and Vulkan the vertex stage contains all stages (documented). A missing program returns
  success with empty bytecode. On platforms without constant buffers, all globals are reported as one cbuffer.
- `pass.PreprocessVariant(stage, keywords, platform, target, stripLineDirectives: true).PreprocessedCode` returns the
  fully preprocessed HLSL that Unity passes to the compiler: every include expanded, every `#if` resolved.

`Fuluma/Assets/Editor/SpotLightShaderDump.cs` (dev project) does this for the spot light shader, into a `SpotLight/` subfolder (add
entries to its `Shaders` list to dump variants side by side). It covers both passes, all three depth-source variants, and
the vertex and fragment stages, for D3D11, D3D12, Vulkan, GLES3x and Metal. It also writes the preprocessed D3D source
and a `report.txt` with sizes and compiler messages (with file and line).

File names are `<pass index><pass name>_<platform>_<variant>_<stage>.<ext>`, for example
`SpotLight/1FulumaSpotLightOutside_D3D11_default_Fragment.dxbc`. The two passes differ only in render state and one vertex-stage
`#define`, so their fragment programs are identical; examples below use pass 1.

- Editor: **Tools > Fuluma > Dump Spot Light Shader** (writes to `Temp/FulumaShaderDump` and reveals it).
- Batch mode (the editor must not have the project open):

```bash
"C:/Program Files/Unity/Hub/Editor/6000.7.0b4/Editor/Unity.exe" -batchmode -quit -projectPath "M:/Unity/Projects/Fuluma/Fuluma" -executeMethod Saesentsessis.Fuluma.Editor.Tools.SpotLightShaderDump.DumpFromCommandLine -fulumaShaderDumpDir "C:/temp/fuluma-shader-dump" -logFile "C:/temp/fuluma-shader-dump.log"
```

### What each platform returns (verified, `forExternalTool: true`)

| `ShaderCompilerPlatform` | Bytes | Magic / start | Read it with |
|---|---|---|---|
| `D3D` (D3D11) | DXBC container (FXC, shader model 5) | `DXBC` | `fxc -dumpbin` |
| `D3D12` | DXBC, byte-identical to D3D11 for this shader: FXC is the default, DXC only with `use_dxc` (section 6) | `DXBC` | `fxc -dumpbin` |
| `Vulkan` | Raw SPIR-V (not SMOL-V: an old bug that returned compressed SMOL-V was fixed in 2021.2/2022.2) | `0x07230203` (bytes `03 02 23 07`) | `spirv-dis`, `spirv-cross`, `malioc --spirv`, RGA |
| `GLES3x` | GLSL ES source text (`#version 310 es` here, because of `#pragma target 4.5`) | `#version` | read directly, `malioc` |
| `Metal` | Unity binary container, not plain MSL | `FE CA 0D F0` | use the Inspector (MSL) or Xcode (section 6) |

## 4. Disassemble and count (verified for D3D)

Windows SDK tools are under `C:/Program Files (x86)/Windows Kits/10/bin/<version>/x64/` (`fxc.exe`, `dxc.exe`). The Unity
editor ships `D3DCompiler_47.dll`, `dxcompiler.dll` and `spirv-cross-c-shared.dll` (libraries, not command-line tools).

```bash
"C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe" -nologo -dumpbin -Fc SpotLight/1FulumaSpotLightOutside_D3D11_default_Fragment.asm SpotLight/1FulumaSpotLightOutside_D3D11_default_Fragment.dxbc
```

The listing has the input/output signatures, resource declarations (`dcl_resource_structured`, `dcl_temps`) and one
instruction per line. The `// Approximately N instruction slots used` footer said 0 for this shader, so count the
instructions yourself:

```bash
awk '/^ *(dcl_|\/\/|vs_|ps_)/{next} /^ *[a-z]/{n++; split($1,a,"_"); op[a[1]]++} END{print n; for (k in op) print k, op[k]}' SpotLight/1FulumaSpotLightOutside_D3D11_default_Fragment.asm
```

`docs/dev/Scripts/dxbc_cost.py` goes further:
- It counts scalar operations by class: ALU, transcendental (SFU), and memory. A vec3 `mul` counts as 3, `div` as `rcp` + `mul`.
- It does so for the whole program and for its shortest and longest path through `if`/`else`/`endif`, like `malioc`'s
  shortest and longest path.
- It applies two simple throughput models, desktop (Ampere-like) and mobile (Valhall-like); section 10 uses them.

```bash
python "docs/dev/Scripts/dxbc_cost.py" SpotLight/1FulumaSpotLightOutside_D3D11_default_Vertex.asm SpotLight/1FulumaSpotLightOutside_D3D11_default_Fragment.asm
```

DXBC counts are a static, vendor-neutral proxy. The driver recompiles DXBC/SPIR-V into the GPU's ISA, so use them to
compare versions of the same shader, not as cycle counts. Per-GPU costs come from section 5.

To compile the preprocessed source yourself (to try a change without reimporting, or to read warnings in context):

```bash
"C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe" -nologo -T ps_5_0 -E FulumaSpotLightFragment -Fc out.asm 1FulumaSpotLightOutside_D3D11_default_Fragment.preprocessed.hlsl
```

## 5. Per-GPU static analysis: vendor offline compilers (documented)

None of these are installed here.

- **Arm Mali Offline Compiler (`malioc`)**: free, part of Arm Performance Studio (Windows/Linux/macOS, Arm account to
  download).
  - Input: ESSL (the `GLES3x` dump), Vulkan SPIR-V (`--spirv`) or Vulkan GLSL.
  - Reports cycles per unit (A arithmetic, LS load/store, V varying, T texture) for the total, shortest and longest
    path, and the bounding unit. Also work/uniform registers, stack spilling and the share of 16-bit arithmetic.
  - Feed one stage per file; multiple files are concatenated.

  ```bash
  malioc --core Mali-G76 --fragment 1FulumaSpotLightOutside_GLES3x_default_Fragment.gles3
  malioc --vulkan --spirv -y main --fragment 1FulumaSpotLightOutside_Vulkan_default_Fragment.vulkan
  ```
  The GLES dump is a full program text; if `malioc` rejects it, split out the fragment part.
- **Qualcomm Adreno Offline Compiler**: via Qualcomm Package Manager / Software Center. Reports instruction counts,
  register use and memory access (Snapdragon Profiler uses it). Command-line flags are documented in the install
  (`OfflineCompiler.html`); unverified here.
- **AMD Radeon GPU Analyzer (RGA)**: MIT-licensed. Offline modes need no AMD driver or hardware. Reports ISA, VGPR/AGPR,
  LDS and scratch usage, live registers and the control-flow graph.
  - `rga -s dx11` takes HLSL/DXBC through the d3dcompiler DLL; `-s dx12` needs the AMD driver.
  - `rga -s vk-offline-spv` takes a SPIR-V binary such as `1FulumaSpotLightOutside_Vulkan_default_Fragment.vulkan`.

## 6. In a captured frame: RenderDoc, PIX, Xcode (documented)

- **Debug symbols:** `#pragma enable_debug_symbols` generates symbols for Vulkan, D3D11/12 and supported consoles, and
  **disables optimizations for all APIs**. Use it for debugging only, never for instruction counts. Whether the older
  `#pragma enable_d3d11_debug_symbols` still works as an alias in 6000.7 is unverified. Unity staff say Vulkan debug
  symbols don't currently show up in external tools.
- **DXC:** FXC is the default. DXC is production-ready only for Windows D3D12 (experimental on Vulkan and Metal, not
  for D3D11).
  - Per shader: `#pragma use_dxc` (DXIL), optionally per API (`#pragma use_dxc dx12`); `#pragma never_use_dxc` forces
    FXC. These pragmas override the build profile.
  - Project-wide: Build Profiles → Windows → Graphics Settings → Shader Build Settings → Shader Compiler Backend
    Selection → Direct3D12 → DXC. Building for D3D11 and D3D12 then produces two shader sets.
  - Watch-outs for this shader under DXC: `atan2(0, 0)` and `round(0.5)` differ from FXC. Our `atan2` arguments are
    never both 0, because 1 + u0·u1 > 0 whenever u1 = u0.
- **RenderDoc** (installed here: `C:/Program Files/RenderDoc`): right-click the Game or Scene view tab → **Load
  RenderDoc**, or start the editor with `-load-renderdoc`, then use the capture button. It shows the disassembly, and
  shader debugging works on D3D11, D3D12 (DXIL since v1.36) and Vulkan. Source-level debugging needs
  `enable_debug_symbols`, plus `use_dxc` on D3D12.
- **PIX** (D3D12): build a Windows x64 Development Build with `#pragma enable_debug_symbols`, launch it from PIX with
  "Launch for GPU Capture", then Pipeline → Debug Pixel. Recent PIX versions add static ISA, register-pressure and
  dependency analysis, and live per-instruction profiling on AMD and Intel GPUs. The latter needs shader source and PDBs.
- **Xcode** (Metal): a GPU frame capture shows per-line cost in the shader editor, a cost heat map, and a Shader Cost
  Graph (needs A17 Pro / M3 or newer). Include shader source in the capture.

## 7. Unity's own tools (documented)

- **Frame Debugger:** per draw it shows the used shader, pass, LightMode, the keywords table, properties, buffers,
  render state and counts. No GPU timing. Use it to confirm which variant (depth source) actually ran.
- **GPU Usage Profiler module:** D3D11, D3D12 and OpenGL. Not Vulkan, not Metal (use Xcode). Editor Play-mode profiling
  works on D3D11/D3D12 only.
- **Project Auditor** (built in since 6.4): passes, keywords and variant counts, and compiled variants after a build. No
  instruction cost.

## 8. Measure on the GPU (verified)

`FulumaSpotLightRenderPassTests.Benchmark_ManyOverlappingLights` renders 64 large overlapping lights into a 1024x1024
HDR target through the project's URP renderer:
- 40 frames, then a 1-pixel readback to wait for the GPU;
- minus the same scene without lights;
- median of 5 runs.

It only runs when `FULUMA_BENCHMARK` is set:

```bash
FULUMA_BENCHMARK=1 "C:/Program Files/Unity/Hub/Editor/6000.7.0b4/Editor/Unity.exe" -batchmode -projectPath "M:/Unity/Projects/Fuluma/Fuluma" -runTests -testPlatform EditMode -assemblyNames Saesentsessis.Fuluma.Editor.Tests -testFilter Benchmark_ManyOverlappingLights -testResults results.xml -logFile log.txt
```

`Benchmark_LightsBehindWall` measures the same way, for 64 lights 10–16 units away (the camera is outside every
light) behind a wall that hides most of them: it shows how much work goes into light pixels that end up occluded. Run
both with `-testFilter "Benchmark_"`.

The result is a line starting with `BENCHMARK` in `results.xml`. It is for comparing shader changes on one machine.
It is not a frame budget, and it doesn't represent tile-based mobile GPUs (use section 5 and on-device captures for those).

## 9. Worked example: optimizing the spot light fragment shader (verified)

Same machine and benchmark for every row. Accuracy was re-checked after each step against a brute-force integral
(`Render_MatchesBruteForceIntegral`): unchanged, worst 0.9% / 1.7% / 0.7% for the three views.

| Step | DXBC fragment instructions | `div` | `sincos` | buffer loads | GPU ms (64 lights, 1024²) |
|---|---|---|---|---|---|
| Baseline: quadrature with 2 `atan` + 3 `tan` per segment, light decoded per pixel, depth via world position | 748 | 62 | 14 | 3 | 2.45 |
| A: nodes from tangent identities (1 `atan2` + 1 `tan` per segment); the Jacobian cancels the falloff divide | 633 | 26 | 6 | 3 | 2.14 |
| B: light decoded once per vertex, passed as flat varyings | 608 | 26 | 4 | 1 | 1.95 |
| C: scene distance from linear eye depth instead of a world-position reconstruction | 615 | 28 | 4 | 1 | 1.88 |
| D: lights the camera is clearly outside of are drawn front faces with a depth test (early-Z) | 615 | 28 | 4 | 1 | 1.86 |

- **Total:** −24% GPU time in the open scene.
- **Step D** is not a fragment-shader change, so the open scene (camera inside or near most lights) stays the same. In
  `Benchmark_LightsBehindWall` it took the lights from 0.52 ms to about 0.28 ms: occluded pixels are now rejected before
  the fragment program runs. Each instance goes through the vertex shader once per pass (the pass that doesn't own it
  collapses it), so vertex work doubles; the vertex shader is 110 DXBC instructions over a small proxy.
- **Step C** adds instructions statically (the orthographic fallback is compiled in) but shortens the executed
  perspective path.
- **The vertex shader** grew from 70 to 84 instructions in step B; that runs once per proxy vertex rather than per pixel.
- **The dump also caught a warning.** FXC reported "use of potentially uninitialized variable" in `FulumaSpotQuadrature`
  (`FulumaSpotLightScattering.hlsl:128`), its usual false positive for an early `return` in code that gets unrolled.
  Restructuring the segment function to a single exit removed it.

## 10. Estimated cycles per pixel and vertex (verified)

Costs come from `dxbc_cost.py` (section 4) on the D3D11 DXBC, pass 1, default variant. The longest path is a ray through
the inner cone (3 segments); the shortest is a ray that misses the volume.

The two throughput models describe the instruction mix, not vendor-compiler output. Use them to compare shader versions,
not as absolute numbers:
- **Desktop (Ampere-like SM partition):** 32 FP32 lanes and 4 SFU lanes per clock, so per 32-pixel warp the cost is
  max(ALU + SFU, 8 × SFU).
- **Mobile (Valhall-like arithmetic unit):** 16-wide warps, FMA 1 cycle and SFU 4 cycles, so per warp the cost is
  ALU + 4 × SFU.

| Program | DXBC instructions | Path | Scalar ALU / SFU ops | Desktop, cycles per warp → per invocation | Mobile, cycles per warp → per invocation |
|---|---|---|---|---|---|
| Fragment | 615 | longest | 551 / 45 | 596 → ~18.6 | 731 → ~46 |
| Fragment | | shortest (miss) | 154 / 8 | 162 → ~5 | 186 → ~12 |
| Vertex | 110 | (no branches) | 174 / 18 | 192 → ~6 | 246 → ~15 |

The fragment stage is ALU-bound in both models; its one buffer load per pixel is the scene depth.

A cheaper shader with one sample per segment instead of three was measured this way and dropped:
- static and modeled cost −20…−27%, GPU time −18%;
- 7–33% mean error with visible seams.

The numbers are in the README of `Assets/Archive/SpotLightSampling`.

## Sources

- Shader Importer (Compile and show code, Preprocess only): https://docs.unity.com/en-us/engine/6000.6/manual/materials-and-shaders/class-shader-importer
- Compute shaders compiled for all APIs in 6.3: https://discussions.unity.com/t/compute-shader-being-compiled-for-all-graphics-apis-after-upgrade-to-6-3/1701755
- Compile and show code in Packages: https://issuetracker.unity3d.com/issues/shader-preprocess-only-option-and-compile-and-show-code-do-nothing-when-clicked-if-shader-is-in-packages-folder
- Pragma directives (`enable_debug_symbols`, `use_dxc`): https://docs.unity.com/en-us/engine/6000.7/manual/materials-and-shaders/shaders/reference/sl-pragma-directives
- DXC in Unity: https://docs.unity.com/en-us/engine/6000.7/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reducing/shader-dxc-compiler
- Vulkan debug symbols: https://discussions.unity.com/t/any-way-to-generate-vulkan-shader-debug-symbols-for-shader-profiling/1553703
- `CompileVariant`: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/shaderdata/pass/compilevariant
- `VariantCompileInfo`: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/shaderdata/variantcompileinfo
- SMOL-V issue: https://issuetracker.unity3d.com/issues/vulkan-shaderdata-dot-pass-dot-compilevariant-function-compile-data-in-the-smol-v-format-when-using-vulkan
- Mali Offline Compiler: https://learn.arm.com/learning-paths/mobile-graphics-and-gaming/ams/malioc/ and https://learn.arm.com/install-guides/ams/
- Adreno Offline Compiler: https://www.qualcomm.com/developer/blog/2025/08/optimize-performance-and-graphics-for-adreno-gpu-low-power-gaming
- Radeon GPU Analyzer: https://gpuopen.com/rga/ and https://github.com/GPUOpen-Tools/radeon_gpu_analyzer
- Xcode shader inspection: https://developer.apple.com/documentation/xcode/inspecting-shaders
- PIX in Unity: https://docs.unity.com/en-us/engine/6000.7/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-debugging/debugging-shaders-with-pix
- RenderDoc in Unity: https://docs.unity.com/en-us/engine/6000.7/manual/analysis/graphics-performance-profiling/profiling/render-doc-integration
- Frame Debugger: https://docs.unity.com/en-us/engine/6000.7/manual/analysis/graphics-performance-profiling/profiling/frame-debugger-window-event-information
- GPU Usage Profiler: https://docs.unity.com/en-us/engine/6000.7/manual/analysis/graphics-performance-profiling/profile-rendering/profiler-gpu
