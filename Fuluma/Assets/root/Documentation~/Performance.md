# Performance

Measured costs of Fuluma on mobile and desktop hardware, with the methodology behind every number. Values on other
hardware and in other scenes will differ: test on your target platforms.

## Methodology

**Mobile** (measured 2026-11-23, Fuluma 0.1.0, Unity 6000.7.0b4, URP 17.7):

- **Devices:** Xiaomi Redmi 15C (Mali-G52 MC2, 1600×720) and Xiaomi 11T (Mali-G77 MC9, 2400×1080), both on Vulkan at
  native resolution, MSAA off unless a table says otherwise.
- **Scene:** the development project's stress scene: 1024 static spot lights with random colors, ranges of 4 to 6
  units and outer angles of 60 to 90 degrees, spread through a 100 × 20 × 100 box with 300 random occluder boxes (2 to
  10 units); a fixed camera pose from which 433 lights are visible. Lights don't move during a run.
- **Settings:** the render feature's defaults, pinned from the command line (Isotropic, Range Scaled, no glare, event
  After Rendering Skybox, Depth Aware upsample, All lights in the buffer, Auto timing, Scene Depth, Near Plane Fade
  and LODs on, Wide Cone Precision off); each run changes one thing.
- **Build:** a development IL2CPP ARM64 player that keeps every Fuluma shader variant, so settings can switch at run
  time.
- **Measure:** each configuration runs 22 seconds; the result is the mean of the last 3 one-second averages of the
  Mali counter *GPU active cycles per frame* (read through Unity's `com.unity.profiling.systemmetrics.mali`). Cycles
  rather than milliseconds, because the GPU clock moves (DVFS) and vsync rounds frame times. Two rounds, interleaved
  (all configurations, then all again): the two rounds agreed within 2%, most within 1%; the thermal status stayed
  at 0 throughout.
- **Percentages** compare a configuration with the baseline at the same light buffer scale.

**Desktop** (measured 2026-11-23): RTX 3060 on Direct3D 12, a development Windows player of the same stress scene with
16,384 lights at native 4K (3840×2160, fullscreen), 6321 of them visible.

- **Feature costs:** the same configurations and pinned defaults as on the phones, with 300 occluders; GPU frame time
  from Unity's `FrameTimingManager` at normal clocks, mean of the last 3 one-second averages of a 24-second run. Each
  run holds steady within 0.5%, but the card's boost clock settles differently per launch (whole runs land up to
  about 5% apart), so four interleaved rounds were averaged: differences under about 3% are within that noise.
- **Light passes:** Nsight Graphics GPU Trace with clocks locked to base (about 35% slower than in normal play), no
  occluders, 600 warm-up frames, then 3 traced frames. The 2026-11-23 traces match the 2026-11-18 ones within 1%.

**CPU:** main-thread and worker times from Fuluma's profiler markers, averaged per frame, in the same phone runs;
culling at scale from an Editor benchmark on an Intel i5-11400F.

## Mobile GPU (Mali-G52, Mali-G77)

### Light buffer scale

| Scale               |   Mali-G52    |   Mali-G77   |
|---------------------|:-------------:|:------------:|
| 1 (full resolution) | 39.0 M cycles | 9.0 M cycles |
| 0.5 (default)       | 17.0 M (−56%) | 4.7 M (−48%) |
| 0.25                | 10.9 M (−72%) | 3.5 M (−61%) |
| 0.1                 | 9.6 M (−75%)  | 3.6 M (−60%) |

A 0.5 buffer about halves the GPU cost. Below 0.25 the buffer's fixed cost (depth downsample, composite, extra
render passes) dominates, and smaller scales save little more.

### Light buffer options (scale 0.5)

| Option                                                                | Mali-G52 | Mali-G77 |
|-----------------------------------------------------------------------|:--------:|:--------:|
| Upsample Bilinear instead of Depth Aware                              |   −5%    |   −3%    |
| Lights Large Only instead of All (threshold 0.15)                     |   +25%   |   +16%   |
| Timing Before Opaques instead of Auto (no depth prepass in the scene) |  −0.5%   |  +1.3%   |

On these tile-based GPUs, *All* is the cheaper choice. Before Opaques saves nothing here because the scene has no
depth prepass to reuse: it forces one.

### MSAA

With MSAA 4x the light buffer can't read the camera's multisampled depth attachment; URP copies the depth to a
texture and the buffer reads that copy.

| Scale | Mali-G52, MSAA off → 4x | Mali-G77, MSAA off → 4x |
|-------|:-----------------------:|:-----------------------:|
| 1     |      39.0 → 46.2 M      |       9.0 → 9.7 M       |
| 0.5   |      17.0 → 32.1 M      |      4.7 → 14.0 M       |
| 0.25  |      10.9 → 26.2 M      |      3.5 → 12.7 M       |
| 0.1   |      9.6 → 24.6 M       |      3.6 → 13.1 M       |

With MSAA, a downsampled buffer loses most of its advantage, and on the Mali-G77 it costs more than full resolution.
Before Opaques timing (the buffer then reads URP's depth prepass) recovered 36% (Mali-G52) and 64% (Mali-G77) at
scale 0.5.

### Clip boxes

The scene is divided into 12-unit rooms, and every light gets its room's boxes; costs are compared with the same
lights without boxes.

| Boxes per light                   | Mali-G52, full / 0.5 | Mali-G77, full / 0.5 |
|-----------------------------------|:--------------------:|:--------------------:|
| One Keep box                      |      +6% / +2%       |       0% / −1%       |
| Keep + Subtract                   |      +9% / +7%       |      +3% / +1%       |
| Two Keep boxes                    |     +22% / +16%      |      +14% / +6%      |
| Two Subtract boxes                |     +36% / +24%      |     +24% / +11%      |
| Three boxes (Keep + two Subtract) |     +70% / +44%      |     +47% / +22%      |

Lights without boxes keep the cheapest shader; one Keep box per room is nearly free, and each further box adds an
integrated piece on the pixels that have it.

### Fidelity features

| Change from the defaults             | Mali-G52, full / 0.5 | Mali-G77, full / 0.5 |
|--------------------------------------|:--------------------:|:--------------------:|
| Scene Depth off                      |     +97% / +58%      |     +74% / +33%      |
| Scene Depth off, at scale 0.25 / 0.1 |     +19% / −20%      |      +6% / −21%      |
| Near Plane Fade off                  |       0% / 0%        |       0% / 0%        |
| Wide Cone Precision on               |      +4% / +3%       |      +1% / +1%       |
| Spot Light LODs off                  |      +2% / +2%       |      +1% / +1%       |

Scene Depth pays for itself through occlusion unless the buffer is very small. Near Plane Fade only costs on lights
the camera is near or inside of, which this view has few of.

### Scattering and pass event

| Change from the defaults                                        | Mali-G52, full / 0.5 | Mali-G77, full / 0.5 |
|-----------------------------------------------------------------|:--------------------:|:--------------------:|
| Henyey-Greenstein phase (anisotropy 0.7)                        |     +14% / +11%      |      +7% / +4%       |
| Unity distance falloff                                          |      +3% / +3%       |       0% / 0%        |
| Point lamp glare                                                |       0% / 0%        |       0% / 0%        |
| Event After Rendering Opaques instead of After Rendering Skybox |       0% / 0%        |       0% / 0%        |

Henyey-Greenstein is the only scattering option with a noticeable cost; drawing after the skybox costs nothing.

## Desktop GPU (RTX 3060)

16,384 lights at native 4K.

### Light buffer scale

| Scale               | GPU frame (normal clocks) |
|---------------------|:-------------------------:|
| 1 (full resolution) |          21.9 ms          |
| 0.5 (default)       |       6.5 ms (−70%)       |
| 0.25                |       2.5 ms (−89%)       |
| 0.1                 |       2.2 ms (−90%)       |

At this density many lights overlap on every pixel and the full-resolution frame is shader-bound (87% of the SMs'
instruction throughput); a 0.5 buffer cuts it to under a third.

### Light passes (Nsight, base clocks, no occluders)

| Light buffer                 | GPU frame | Light passes                                                                          |
|------------------------------|:---------:|---------------------------------------------------------------------------------------|
| Full resolution              |  59.3 ms  | 59.0 ms                                                                               |
| Scale 0.5, All               |  16.7 ms  | 16.0 ms, plus 0.4 ms downsample and composite                                         |
| Scale 0.5, Large Only (0.15) |  30.0 ms  | 18.6 ms full resolution + 10.8 ms in the buffer, plus 0.4 ms downsample and composite |

### Features

| Change from the defaults                                        | Full resolution | Scale 0.5 |
|-----------------------------------------------------------------|-----------------|-----------|
| Scene Depth off                                                 | +90%            | +80%      |
| Scene Depth off, at scale 0.25 / 0.1                            | +61% / −1%      |           |
| One Keep box                                                    | +6%             | +8%       |
| Keep + Subtract                                                 | +12%            | +14%      |
| Two Keep boxes                                                  | +25%            | +22%      |
| Two Subtract boxes                                              | +37%            | +34%      |
| Three boxes (Keep + two Subtract)                               | +81%            | +74%      |
| Henyey-Greenstein phase (anisotropy 0.7)                        | +10%            | +9%       |
| Wide Cone Precision on                                          | +3%             | +3%       |
| Unity distance falloff                                          | +3%             | +3%       |
| Near Plane Fade off                                             | −1%             | 0%        |
| Spot Light LODs off                                             | 0%              | +1%       |
| Point lamp glare                                                | 0%              | 0%        |
| Event After Rendering Opaques instead of After Rendering Skybox | +1%             | +1%       |
| Upsample Bilinear instead of Depth Aware                        |                 | 0%        |
| Lights Large Only instead of All (threshold 0.15)               |                 | +15%      |
| Timing Before Opaques instead of Auto                           |                 | +1%       |

Desktop GPUs follow the phones in direction but not in size: clip boxes and Henyey-Greenstein cost about as much as
on the Mali-G52, and turning Scene Depth off never pays off here (the buffer's fixed cost is small on this GPU).

## CPU

**Per frame on the phones** (1024 lights, 433 visible, full resolution; main thread unless noted):

| Step                                               | Mali-G52 phone | Mali-G77 phone |
|----------------------------------------------------|:--------------:|:--------------:|
| Culling: schedule                                  |    0.089 ms    |    0.074 ms    |
| Culling: job (all workers)                         |    0.103 ms    |    0.052 ms    |
| Culling: wait                                      |    0.005 ms    |    0.004 ms    |
| Culling: index upload                              |    0.019 ms    |    0.020 ms    |
| GPU mirror upload (static lights, nothing changed) |    0.016 ms    |    0.012 ms    |
| Material binding                                   |    0.014 ms    |    0.010 ms    |
| Draw pass recording and draws                      |    0.258 ms    |    0.141 ms    |

Culling runs on worker threads while URP culls and records, so the main thread mostly pays for scheduling and the
draws. Turning Spot Light LODs off made no measurable difference to culling here. In the desktop runs (16,384 lights,
i5-11400F), the culling job took 0.34 ms across the workers and the draw pass 0.12 ms on the main thread.

**Per draw** (2026-11-19): each instanced draw costs about 22 µs of main-thread time on the Mali-G52 phone. With clip
boxes, every further clip class in use adds draws of about 35 µs each (17 µs on the Mali-G77).

**Culling at scale** (65,536 static lights, Editor benchmark, i5-11400F):

| Job                                                   | 11 worker threads |  One thread  |
|-------------------------------------------------------|:-----------------:|:------------:|
| With LODs                                             |  0.065–0.093 ms   | 0.40–0.53 ms |
| One LOD (Spot Light LODs off or a one-LOD descriptor) |  0.063–0.070 ms   |      -       |
| One LOD, Large Only split                             |  0.077–0.082 ms   |      -       |

The index upload stayed under 0.016 ms.

**Dynamic clip boxes** (162 boxes following Transforms): the mirror's main-thread upload took 0.12 ms on the Mali-G52
phone and 0.08 ms on the Mali-G77 phone; the copy itself runs on a worker.

## Memory

Per item of a store's capacity:

|            | CPU                                          | GPU                                                           |
|------------|----------------------------------------------|---------------------------------------------------------------|
| Spot light | 88 B, plus the slot allocator's bookkeeping  | 64 B, plus 16 B per dynamic buffer (112 B with the default 3) |
| Clip box   | 140 B, plus the slot allocator's bookkeeping | 48 B, plus 48 B per dynamic buffer (192 B with the default 3) |

- **CPU** memory is allocated with a store's first light or box and grows as needed. Dynamic lights and boxes also
  take an entry in the store's `TransformAccessArray`.
- **GPU** buffers start at **Spot Light Buffer Size** (at least 64, rounded up to a power of two) and **Clip Box
  Buffer Size** (at least 16) and grow as needed; the defaults allocate 28 KB for lights and 3 KB for clip boxes.
  **Dynamic Buffer Count** sets the dynamic buffers. The render feature's inspector shows these sizes under each
  field.
- Each camera also keeps a GPU list of its visible lights (4 B per entry).
