# Spot Light Proxy Array

Asset (`ScriptableObject`): `SpotLightProxyArrayDescriptor`, created from **Assets > Create > Saesentsessis > Fuluma > Spot Light Proxy Array Descriptor**

The proxy mesh LODs a render feature draws spot lights with, picked per light by its size on screen. When the
feature has none, a built-in set is used.

<!-- Image: a Spot Light Proxy Array asset's inspector with its LOD list, next to wireframes of a fine and a coarse proxy. -->

## Overview

Each fake spot light is drawn as a proxy mesh: a cone with a rounded cap that encloses the light's volume. The shader
computes the glow exactly for every pixel the proxy covers, so the proxy's resolution decides only how tightly it
encloses the volume, not how the glow looks.

- **Bands.** A LOD is a list of band resolutions: vertices per ring, from the rim (which is also the cone side's
  resolution) toward the tip of the cap. Each band needs at least 3 vertices.
- **Screen sizes.** A light uses the first LOD whose screen size it reaches (its bounding-sphere diameter over the
  screen height); the last LOD takes every smaller light. Screen sizes must not increase from one LOD to the next.
- **The trade-off.** A coarse proxy has fewer vertices, but it must be inflated more to still enclose the cone, so
  it covers more pixels that miss the volume. Fewer vertices save vertex work and add fragment work.

Assign the asset to the render feature's **Spot Light Descriptor**. Without one, the feature uses a built-in set of
three LODs:

| LOD | Band resolutions | Screen size |
|---|---|---|
| 0 | 16, 12, 9, 7, 4 | ≥ 0.25 |
| 1 | 12, 9, 7, 4 | ≥ 0.08 |
| 2 | 10, 7, 4 | the rest |

## Properties

| Property | Description | Default         | Range | Runtime change |
|---|---|-----------------|---|---|
| LODs | Proxy LODs, finest first, with non-increasing screen sizes. At most 8 are used. | None (built-in) | 1 to 8 | Applies when a feature's pass is next recreated (see Notes) |
| LODs > Band Resolutions | Vertices per ring, from the rim toward the cap tip. | Per LOD         | ≥ 3 each | As above |
| LODs > Screen Size | Smallest screen size drawn with this LOD; smaller lights use the next one. | Per LOD         | ≥ 0 | As above |

## Scripting

| Member | Description |
|---|---|
| `LodCount` | Number of LODs. |
| `Lods` | The LODs (`SpotLightProxyDescriptor`: `ScreenSize`, `BandResolutions`). |

Assigning a descriptor from code:

```C#
using Saesentsessis.Fuluma.Rendering.Proxy;
using Saesentsessis.Fuluma.Rendering.URP;

public static void UseProxies(FulumaRenderFeature fuluma, SpotLightProxyArrayDescriptor proxies)
{
    fuluma.SpotLightDescriptor = proxies; // Recreates the pass; null returns to the built-in set.
}
```

## Notes

- **Spot Light LODs off.** With the Spot Light LODs fidelity feature off, every light uses LOD0 only.
- **Edits apply when the pass is recreated.** The render feature builds its proxy mesh when its pass is created,
  and doesn't notice edits to an asset it already uses. After editing one, clear the feature's Spot Light
  Descriptor field and assign the asset again, or change any other setting that recreates the pass.
