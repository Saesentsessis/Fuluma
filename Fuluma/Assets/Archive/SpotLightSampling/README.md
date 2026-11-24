# Archived spot light sampling modes

Not part of the package (`Assets/root`): kept for reference only. Nothing includes these files.

`FulumaSpotLightScattering_AllModes.hlsl` is the scattering include as it was before the package kept only the
Quadrature mode (plus the Raymarch reference). It still contains every removed mode:

| Keyword | `SpotLightSampling` value (then) | What it did | Why it was dropped |
|---|---|---|---|
| `_FULUMA_SAMPLING_POINT` | `PointSample` | Three segments split at the inner cone (penumbra-only rays split at the angular peak); each = length × distance falloff × angular falloff at the midpoint. | Midpoint rule badly underestimates inverse square along long segments (dark disc from inside the cone). |
| `_FULUMA_SAMPLING_INTEGRAL` | `IntegratedDistance` | Same segments; closed-form distance-falloff integral × angular falloff at one point (midpoint in t, or midpoint in the airlight angle φ for inverse square). | Single angular sample per segment: 1.2–2× too bright on penumbra rays; needed the φ trick to avoid seams. |
| `_FULUMA_SAMPLING_SHELLS` | `ShellSum` | No angular sampling: ½ × (falloff integral inside the outer cone + inside the inner cone). | Two-level look: the inner cone shows as an edge by design. |

`FulumaSpotLightScattering_QuadratureRaymarchFalloffs.hlsl` is the include from the next stage, when the package had
two sampling modes and a falloff choice. Removed from it afterwards:

| Keyword | Setting (then) | What it did |
|---|---|---|
| `_FULUMA_SAMPLING_RAYMARCH` | `SpotLightSampling.Raymarch`, `SpotLightRaymarchStepSize` | Reference raymarch of the same integrand with a fixed world-space step (max 1024 steps), jittered per pixel. Matched a brute-force integral within ~0.4% at a 0.005 step; the quadrature within ~1–2%. Used to compare the fake light with a real raymarch: visually indistinguishable. |
| `_FULUMA_FALLOFF_SMOOTH_WINDOW` | `SpotLightFalloff.SmoothWindow` | f(d) = (1 − d²/r²)² × 15/(16r). |
| `_FULUMA_FALLOFF_LINEAR` | `SpotLightFalloff.Linear` | f(d) = (1 − d/r) / r. |

The package kept inverse square (1/(d² + ε²) × window × r/(4π), ε = 2% of the range) as its only falloff.

Also not kept: an "Exact" mode with the closed-form antiderivative of inverse square × window × angular falloff. It was
exact in float64 but broke in float32 (cancellation in the expanded (cos − cosOuter)² terms: 64% error at the edges of a
60° cone, over 1000× for narrow cones). The Python verification scripts lived in the session scratchpad.

## Cheap shader: one sample per segment (removed 2026-11-17)

`FulumaSpotLightCheap.shader.txt` was a second shader, `Hidden/Fuluma/Analytical/SpotLightCheap`, kept as `.txt` so
Unity doesn't compile it here. It was the current shader plus `#define FULUMA_SPOT_ONE_SAMPLE` in both passes. That
define switched the quadrature in `FulumaSpotQuadratureSegment` to a 1-point Gauss-Legendre rule in the airlight angle φ,
with the same segments and intersections and no `tan`:

```hlsl
float tanCenter = (u0 * cos0 + u1 * cos1) / (cos0 + cos1);
#if defined(FULUMA_SPOT_ONE_SAMPLE)
float sum = 2.0 * FulumaSpotSampleValue(ray, tanCenter, light, insideInnerCone, h, inverseRange2);
#else
// ... 3-point rule ...
#endif
result = sum * halfWidth * inverseH;
```

Measured against the 3-point shader (RTX 3060, D3D12):

| | 3 samples | 1 sample |
|---|---|---|
| Fragment DXBC instructions | 615 | 489 |
| Longest path, scalar ALU / SFU ops | 551 / 45 | 429 / 26 |
| Estimated cycles per pixel, desktop / mobile model (`dxbc_cost.py`) | ~18.6 / ~46 | ~14.2 / ~33 |
| Open-scene benchmark, 64 lights at 1024² (3 runs) | 1.79–1.96 ms | 1.48–1.57 ms (−17…−20%) |
| Behind-wall benchmark | ~0.2–0.27 ms | same within noise |
| Brute-force error, worst / mean, 4 views | ≤1.8% / ≤0.4% | 16–94% / 7–33% |

Why it was dropped: it looked really bad, for an 18% saving.
- It drew bright lines along the outer cone's silhouette and seams along the inner cone from the apex.
- With one sample, a penumbra segment takes the angular falloff at the midpoint of the angle it subtends, so the result
  jumps wherever the segment structure changes. That's the same lesson as the single-sample modes above.

To bring a mode back: copy its functions into `Shaders/Analytical/SpotLight/FulumaSpotLightScattering.hlsl`, add the
keyword to the `_FULUMA_SAMPLING_*` `multi_compile_local` in `FulumaSpotLight.shader`, and add the enum value and keyword
name in `FulumaSpotLightRenderPass.cs`.

## Trig-free quadrature (A/B tested and dropped 2026-11-18)

`FulumaSpotLightScattering_TrigFreeSwitch.hlsl` is the scattering include as it was during the test, with both node
placements behind `FULUMA_TRIG_FREE_QUADRATURE` (1 = trig-free, 0 = the kept phi-GL3 rule). The trig-free rule places
the three Gauss-Legendre nodes in u, with psi = phi - center = 2 atan(q u) and q = tan(h/2), instead of in phi:

- tan(h) comes from the segment ends without atan2: tan(D/2) = sin D / (1 + cos D), or (1 - cos D) / sin D past 90
  degrees, with sin D, cos D and 1 all known up to the same factor 1 / (cos0 cos1).
- q = tan(h) / (1 + sqrt(1 + tan(h)^2)); outer nodes tan(psi) = 2qu / (1 - q^2 u^2) at u = sqrt(3/5); each node's
  weight is multiplied by the measure dpsi/du = 2q / (1 + q^2 u^2).
- Per segment: 1 sqrt and a few divisions instead of 1 atan2 + 1 tan. Derived by a research agent and checked in Python
  (`docs/dev/Scripts/Quadrature/`); no published source found.

Measured against phi-GL3 (RTX 3060, D3D12; 4K stress test with frozen lights, Nsight GPU Trace at base clocks):

| | phi-GL3 (kept) | trig-free |
|---|---|---|
| Light pass, 3 frames | 59.1 / 59.1 / 58.8 ms | 57.9 / 58.7 / 57.9 ms (~2% faster) |
| Warp instructions per frame | 7.65e9 | 7.40e9 (-3.3%) |
| XU (rcp, rsqrt, sqrt, sin, cos) instructions | 6.35e8 | 6.71e8 (+5.6%) |
| Stalls on XU-type work (MIO throttle + short scoreboard) | 10.4% | 14.8% |
| DXBC longest path, ALU / SFU | 460 / 40 | 430 / 43 |
| Brute-force error per pixel, worst / mean | 0.3-1.8% / 0.1-0.4% | 0.7-1.8% / 0.1-0.4% |
| 1024² benchmarks | same within noise | same within noise |

Why it was dropped: about 2% on this GPU, for slightly worse accuracy and an unpublished derivation. On NVIDIA the
divisions and roots it adds queue on the same slow XU pipe as the trig it removes. Not measured on mobile (Mali has
hardware atan assist), where the balance may differ.
