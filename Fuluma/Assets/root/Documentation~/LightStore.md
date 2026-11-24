# Light Store

Asset (`ScriptableObject`): `LightStore`, created from **Assets > Create > Saesentsessis > Fuluma > Light Store**

A collection of fake lights and clip boxes held in native memory. Components register in a store, and each render
feature draws one store; `LightStore.Default` is used when none is assigned. Its code API adds, moves, changes and
removes lights and boxes without components.

<!-- Image: a Light Store asset's inspector with its readouts (epoch, light counts and capacities, memory). -->

## Overview

Every light is either **static** (placed from code or on registration, moved only on request) or **dynamic**
(follows a Transform every frame); each kind lives in its own pool. Adding a light returns a `LightHandle`, adding a
clip box a `ClipBoxHandle`; keep the handle to change or remove the light or box later. A light references up to four
boxes of its own store, each as Keep or Subtract (`ClipBoxReference`).

- **The default store.** `LightStore.Default` is created on first use, is never saved, and is what components,
  render features and code use when no store is assigned.
- **Own stores.** A store asset splits lights between render features: see
  [Light Stores & Cameras](../README.md#light-stores--cameras).
- **Memory.** A store allocates its native memory on the first add and releases it when the store is disabled
  (destroyed or unloaded), on a code reload, and on every Editor play-mode switch. Components register again by
  themselves after a release; lights and boxes added from code must be added again.

## Properties

The asset has no settings. Its inspector shows read-only readouts while the store is allocated:

| Readout                      | Description                                                                                                                |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| Epoch                        | The generation of the store's memory; it changes on every allocation and release, and handles from another epoch are dead. |
| Static / dynamic spot lights | Count and capacity of each pool.                                                                                           |
| CPU buffers memory usage     | Native memory of the spot light pools.                                                                                     |

## Scripting

| Member                                                                            | Description                                                                         |
|-----------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| `Default`                                                                         | The shared default store.                                                           |
| `AddSpotLight(Placement, SpotLightData)`                                          | Adds a static spot light; returns its handle.                                       |
| `AddSpotLight(Transform, SpotLightData)`                                          | Adds a dynamic spot light that follows the Transform every frame.                   |
| `SetSpotLight(handle, SpotLightData)`                                             | Replaces a light's color, intensity, range and angles.                              |
| `SetSpotLightPlacement(handle, Placement)`                                        | Moves a static light.                                                               |
| `SetSpotLightPlacements(handles, placements)`                                     | Moves many static lights in one Burst job; returns how many moved.                  |
| `AddClipBox(Placement or float4x4 or Transform, ClipBoxData)`                     | Adds a static box (placement or matrix, scale and shear included) or a dynamic one. |
| `SetClipBoxShape(handle, ClipBoxData)`, `SetClipBoxPlacement(handle, …)`          | Reshapes any box; moves a static box.                                               |
| `SetClipBoxes(light, references)` / `SetClipBoxes(light, ClipBoxSlots)`           | Sets a light's boxes, from handles or from a component's slots.                     |
| `TryGetClipBoxes(light, output, out count)`                                       | Reads a light's boxes back.                                                         |
| `Contains(handle)`                                                                | Whether a light or box handle is still alive in this store.                         |
| `Remove(handle)`                                                                  | Removes a light or a box.                                                           |
| `ClearSpotLights()`, `ClearClipBoxes()`, `Clear()`                                | Removes every light, every box, or both; the memory stays allocated.                |
| `SpotLightCount`, `StaticSpotLightCount`, `DynamicSpotLightCount`, `ClipBoxCount` | Counts.                                                                             |
| `MaxClipBoxes`, `MaxClipBoxesPerLight`                                            | 32767 boxes per store, 4 per light.                                                 |

A component owning lights it adds from code (OnEnable runs again after an Editor play-mode switch, so the lights
come back after a store release):

```C#
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

public class LampRow : MonoBehaviour
{
    [SerializeField] private int count = 10;
    [SerializeField] private float spacing = 3f;

    private LightHandle[] _lamps;

    private void OnEnable()
    {
        var store = LightStore.Default;
        var light = SpotLightData.Default;
        _lamps = new LightHandle[count];

        for (var i = 0; i < count; i++)
        {
            var placement = new Placement
            {
                Position = transform.position + transform.right * (i * spacing),
                Rotation = Quaternion.LookRotation(Vector3.down)
            };

            _lamps[i] = store.AddSpotLight(placement, light);
        }
    }

    private void OnDisable()
    {
        var store = LightStore.Default;
        
        // Remove returns false for handles a store release already killed.
        foreach (var lamp in _lamps)
            store.Remove(lamp);
    }
}
```

Advanced: a dedicated store created at runtime, for example for a procedurally generated level, drawn by a render
feature and freed with the level:

```C#
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Rendering.URP;
using UnityEngine;

var levelStore = ScriptableObject.CreateInstance<LightStore>();
fuluma.OverrideStore = levelStore; // The feature now draws this store.

// ... add the level's lights and boxes to levelStore ...

// Unloading the level:
fuluma.OverrideStore = null;  // Back to LightStore.Default.
Object.Destroy(levelStore);   // Releases its native memory.
```

## Notes

- **Main thread only.** Call the store's API from the main thread.
- **Handles die on release.** After a release (see Overview), every handle issued before is dead: `Contains`
  returns false and the other calls return false or do nothing. Lights and boxes added from code must be added
  again.
- **Limits.** Up to 32767 clip boxes per store and four per light.
