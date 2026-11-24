# Spot light culling: generated assembly

How to read the Burst code of `SpotLightCullingJob` (`Runtime/Rendering/Culling/SpotLightCullingJob.cs`) and what it
showed. General guide to Burst (compilation, caches, getting and reading assembly, Burst 1.8 and 2.0):
`~/.claude/docs/unity-burst.md` (outside the repository: the author's machine-wide notes).

## Dumping it

```bash
bash docs/dev/Scripts/burst_dump.sh AVX2 Windows <out-dir>
bash docs/dev/Scripts/burst_dump.sh X64_SSE4 Windows <out-dir>
bash docs/dev/Scripts/burst_dump.sh X64_SSE2 Windows <out-dir>
bash docs/dev/Scripts/burst_dump.sh ARMV8A_AARCH64 Android <out-dir>
```

Each run writes the whole assembly (`all_<target>.s`) and the job's body (`SpotLightCullingJob_<target>.s`), and
prints the body's instruction count. Close the Editor first, or let it compile your last edit: the script reads
`Library/ScriptAssemblies`.

Differences from the Burst 1.8 guide, on Unity 6000.7 (Burst 2.0):
- `com.unity.burst` is a shim package. The compiler ships with the Editor:
  `Editor/Data/Tools/Burst/Client/bcl/bcl.dll`.
- `bcl.exe` needs .NET 10 (`You must install or update .NET`). Run it as
  `Editor/Data/DotNetSdk/dotnet.exe bcl.dll @response.rsp`.
- `--help` isn't accepted. The options are the same as in 1.8 (`--target`, `--platform`, `--dump=Asm`,
  `--root-assembly`, ...). `--target` must be set explicitly, since `auto` is rejected.
- A batch job's body is labelled `"Unity.Jobs.IJobParallelForBatchExtensions.JobParallelForBatchProducer`1<...SpotLightCullingJob>.Execute(...)"`.
- `[Unity.Burst.CompilerServices.SkipLocalsInit]` is valid on methods only (CS0592 on a struct).

## Findings and changes (Nov 2026)

What the first version compiled to, on every target:

1. **The two 1 KB `stackalloc` lists were zeroed on every batch**: 64 `vmovups ymm` stores on AVX2, 128 `movaps xmm` on
   SSE2/SSE4 and about 100 `stp/str q0` on ARMv8, once per 256 lights. C# `stackalloc` zero-initializes, and nothing reads the memory before it
   is written. Fixed with `[SkipLocalsInit]` on `Execute`.
2. **The near and far planes used scalar dot products**, each a multiply plus 2 shuffles and 3 scalar adds, after the
   packed side-plane test. They are now a second SoA `float4` group (near, far, near, far), so all six sphere
   distances are 6 FMAs (ARM: `fmla v.4s, v.4s, v.s[i]`), a `min` and one compare.
3. **The exact cone-vs-plane test ran per plane** behind six branches, with its own square roots each time. It is now
   two packed evaluations (sides, then near/far) with `select` instead of branches: 2 packed `rsqrt` and a few FMAs. A
   plane the sphere is fully inside can't cull the volume, so the "straddling" mask isn't needed, only one
   "any straddling" branch.
4. **Appending used a branch** per light (inside / outside / culled). Both lists now get the index stored, and the
   counts advance by 0 or 1.

Not changed: `CameraNearProxy` is still scalar with `div` and `rsqrt` and Newton steps. It only runs past its distance
early-out, for lights within reach of the camera, which are few. The camera planes are spilled to the stack on x64:
16 xmm registers can't hold the 8 plane vectors and the temporaries. ARMv8 keeps all of them in registers.

| Target | Instructions in the job body, before → after | Zeroing stores per batch |
|---|---|---|
| AVX2 | 609 → 343 | 64 → 0 |
| X64_SSE4 | 780 → 428 | 128 → 0 |
| X64_SSE2 | 825 → 437 | 128 → 0 |
| ARMV8A_AARCH64 | 496 → 248 | ~100 → 0 |

Instruction counts are static (the whole body, every path), not per-light costs.

Per light, the sphere-reject path (the most common one when lights are spread around the camera) is about 17
instructions plus a 10-instruction loop tail on AVX2, and 16 plus 9 on ARMv8.

Measured with `SpotLightCullingTests.Benchmark_Culling65536Lights` (`FULUMA_BENCHMARK=1`), i5-11400F, Burst AVX2,
65536 static lights, median of 200:

| Case | 11 workers, before → after | 1 thread, before → after |
|---|---|---|
| All around the camera (16.8k visible) | 0.087 → 0.048–0.052 ms | 0.484 → 0.334–0.342 ms (7.4 → 5.1 ns per light) |
| All in front (63.4k visible) | 0.102 → 0.056–0.060 ms | 0.525 → 0.303–0.313 ms (8.0 → 4.7 ns per light) |

The single-thread figure sets `JobWorkerCount = 0`, so jobs run on the main thread. It is the job's own cost, without
scheduling noise, and the better guide for a phone core: expect it to be 2–4× slower per core.

## Proxy LODs (Nov 2026)

The job now also picks each visible light's proxy LOD and sorts it into one of up to nine buckets per pool (inside,
outside LOD0..7), with one stack list per bucket (9 KB, so the body starts with a `__chkstk` probe). What the
visible-light path added:

- `SelectLod`: one distance, two packed compares against the scaled thresholds, two horizontal sums. It runs after
  the frustum test, so culled lights skip it.
- `CameraNearProxy` reads the light's LOD shape with a variable lane index into the per-LOD `float4`s, and, past the
  cheap reject, `atan` and `cos` (`Sleef_cosf_u35`) for the light's exact inflation.
- The append is still branch-free, but the bucket count is a load-increment-store through memory, so consecutive
  lights in the same bucket wait on store forwarding.

| Target | Instructions in the job body, before LODs → with LODs |
|---|---|
| AVX2 | 343 → 492 |
| X64_SSE4 | 428 → 574 |
| X64_SSE2 | 437 → 606 |
| ARMV8A_AARCH64 | 248 → 404 |

| Case | 11 workers | 1 thread |
|---|---|---|
| All around the camera (16.8k visible) | 0.048–0.052 → 0.065–0.074 ms | 0.334–0.342 → 0.41–0.42 ms (6.2–6.5 ns per light) |
| All in front (63.4k visible) | 0.056–0.060 → 0.085–0.090 ms | 0.303–0.313 → 0.49–0.50 ms (7.4–7.7 ns per light) |

Not tried yet: a two-pass bucket sort per batch (store each light's bucket as a byte, then compact) to break the
store-forwarding chain.

`Scripts/burst_dump.sh` now passes `--cache-directory` and runs the compiler from its work folder: without the option,
the compiler writes `Library/BurstCache` into its current directory. Two earlier runs (2026-11-17, before that fix) left caches at the repository root and in
`Runtime/Rendering/Culling/Library` inside the package (deleted 2026-11-19; the `.gitignore` files now ignore any
`Library/BurstCache`).
