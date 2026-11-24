# Cost of single store calls

Measured 2026-11-24 with `Tests/Editor/LightStoreBenchmarks.cs` (`FULUMA_BENCHMARK=1`): 4096 calls per round, 9 rounds,
`Stopwatch.GetTimestamp()` around whole rounds (mean) and around each call (p50/p99/max, 100 ns timer resolution).
Editor 6000.7.0b4, i5-11400F, `-releaseCodeOptimization` (what players compile with; Debug code optimization costs
1.5-2.5x more), collections safety checks on (Editor only; players don't have them). No jobs running, so no call waits
for culling or upload readers. Run it with:

```
FULUMA_BENCHMARK=1 Unity.exe -batchmode -releaseCodeOptimization -projectPath Fuluma -runTests -testPlatform EditMode
  -assemblyNames Saesentsessis.Fuluma.Editor.Tests -testFilter LightStoreBenchmarks -testResults <xml> -logFile <log>
```

`Benchmark_StoreOperations` times the public calls; `Benchmark_StoreOperationSteps` times their parts alone.

## Where the time went

The store's own bookkeeping is cheap: handle checks 14-25 ns, job `Complete()` 3-8 ns, a `NativeList` write 7 ns,
a dirty-range mark 4 ns, `Transform.GetPositionAndRotation` 34 ns. Nearly all of it was `Unity.Mathematics` code run as
plain C# on the main thread (Mono, no SIMD; every `float4` operation a call on a struct copy):

| Step (managed C#) | ns | Paid by |
|---|---:|---|
| `ClipBoxData.Pack` (2 TRS, multiply, determinant, 4x4 inverse) | 1 800-2 300 | every clip box add / set |
| `Placement.ToMatrix` | 340 | clip box placements |
| `SpotLightData.Pack` (cos, half conversions) | 215 | spot light add / set |
| `Placement.Pack` (polar direction) | 200 | spot light add / move |
| `SpotLightCullingData.Place` | 180 | spot light add / move |

The second cost is Unity's `TransformAccessArray`, in native code (step benchmark): `Add` 150 ns,
`RemoveAtSwapBack` of a random element 440-510 ns (of the last element 150 ns), and the indexer write costs the same
as the remove, so overwriting the removed slot with the last transform doesn't help. Rebuilding with `SetTransforms`
costs 250-400 ns per transform, more than the single calls: queueing the changes and rebuilding once per frame would
not save anything.

## Done, without public API changes

1. **Burst kernels for the main-thread packing** (`Runtime/Data/PackingKernels.cs`): `[BurstCompile]` static methods
   called directly from managed code (structs by `in`/`ref`/`out`, `CompileSynchronously`), wrapping the same
   `DataExtensions` math the store's jobs already run under Burst; 25-53 ns per call including the transition.
   `SpotLightPool.Add/SetTransform/SetLight` and the clip box add/set paths call them. Results now match the jobs'
   (Burst) bit for bit; they can differ from the managed `Pack()` in the last bit (`LinkedFakeLightTests` compare
   through the kernel). Without Burst the kernels run as C# with the old cost.
2. **Clip box placements in one kernel call** (`PlaceClipBox`: matrix and packed box) instead of two.
3. **Clip box add appends** when the box takes a new index, instead of growing four lists with `Resize` and then
   overwriting the defaults.

| Call (Release, mean ns) | Before | Burst kernels | Now |
|---|---:|---:|---:|
| `AddSpotLight` (static) | 759 | 204 | 137 |
| `SetSpotLight` (static / dynamic) | 331 / 320 | 79 / 79 | 84 / 85 |
| `SetSpotLightPlacement` | 467 | 157 (best 90) | 90 |
| `SetSpotLightPlacements` (batched, per light) | 8 | 8 | 7 |
| `Remove` (static spot light) | 87 | 80 | 71 |
| `AddSpotLight` (dynamic) | 1 134 | 382 | 352 |
| `Remove` (dynamic spot light) | 942 | 980-1 336 | 830 |
| `AddClipBox` (static / dynamic) | 2 445 / 2 997 | 326-556 / 776 | 126 / 434 |
| `SetClipBoxShape` | 2 352 | 189 | 177 |
| `SetClipBoxPlacement` | 2 438 | 178 | 146 |
| `SetClipBoxes` (1 / 4 boxes) | 54 / 123 | 54-98 / 124-186 | 58 / 135 |
| `Remove` (clip box) | 115-138 | 117-238 | 119-131 |

(Ranges: run-to-run spread; the maximum column has rare 0.1-3 ms spikes from list growth and Editor noise. Same
picture on 6000.3.0f1.)

## What is left per call

- **~70-90 ns floor** for a set: handle check (~15 ns), a job `Complete()` (~5 ns), one kernel call (25-50 ns),
  `NativeList` reads/writes with Editor safety checks (~7 ns each), dirty marks (~4 ns).
- **Adds: ~130 ns** for a static light or box (slot allocator, parallel lists, dirty marks, occasional growth).
- **Dynamic lights and boxes: +0.15-0.75 µs** of `TransformAccessArray` add / swap-back remove, which needs the API or
  the architecture to change (see below).
- Players: no safety checks; IL2CPP compiles the remaining C# natively. Not measured in a player yet (the profiling
  build could run the same calls behind a switch).

## Plan

### Needs public API changes

1. **Batched calls** next to the single ones (like `SetSpotLightPlacements`, 7 ns per light): `AddSpotLights`,
   `RemoveSpotLights`, `SetSpotLights`, `AddClipBoxes`, `SetClipBoxPlacements`, one Burst job each. For dynamic items, a
   batch also lets the `TransformAccessArray` be rebuilt once when most of it changes.
2. **Fewer parallel lists per light** (transforms, lights, culling spheres, culling data, clip box references, plus
   their dirty ranges) is internal, but saves only tens of ns; worth it as part of the deferred design, not alone.

### Later: deferred changes (the direction for the architecture)

Calls record a command instead of changing the pools, and one Burst pass applies all of them at the latest point
(the mirror's upload in `beginContextRendering`, before culling):

- **Record** (main thread, tens of ns): allocate the handle at once (slot + epoch, so the caller gets a usable handle),
  append `{ op, handle, payload }` to a per-store command list. No packing, no job `Complete()`, no list growth per call.
- **Apply** (once per frame): validate (dead or foreign handles, removed-then-set, add-then-remove in the same frame),
  pack, write the pools and dirty ranges, rebuild the `TransformAccessArray` once. Errors go out as error codes
  (Burst can't throw with messages) and are logged after the job.
- **Reads** (`Contains`, `TryGetClipBoxes`, counts) must see pending commands, or be documented as reflecting the last
  applied state; `Contains` can stay exact through the slot allocator.
- **Validation moves**: today every call checks and throws immediately (capacity, null `Transform`, dead handles).
  Deferred, only the cheap checks stay at the call; the rest becomes apply-time reporting. This is the refactor to plan
  with tests for each ordering case.
- Opens the way to recording from other threads (one command list per thread, merged at apply), which the docs
  currently rule out.
