# Fake Light Clip Box

Component: `FakeLightClipBox` (**GameObject > Light > Volumetric > Fake Light Clip Box**, or **Add Component > Light > Volumetric > Fake Light Clip Box**)

An oriented box that limits where fake lights glow. Lights reference up to four boxes, each as Keep (the light
exists only inside) or Subtract (the light is removed inside).

<!-- Image: a selected clip box with its box handle around a room, and a light's Clip Boxes slots referencing it. -->

## Overview

The box registers in its store while the component is enabled. A light uses it once the box is assigned to one of
the light's **Clip Boxes** slots, with a role:

- **Keep**: the light exists only inside its Keep boxes (their union).
- **Subtract**: the light is removed inside the box.

The role belongs to the light's slot, not to the box: one box can be a Keep box for one light and a Subtract box
for another, for example a room that keeps its own lamp in and cuts out a neighbour's beam.

The box's shape is a **Center** and **Size** in its Transform's space, so it follows the Transform's position,
rotation and scale. In the Scene view, a selected box shows a box handle to resize it; other boxes show a wire gizmo.
The inspector starts with a status box saying where the box is registered and which renderers draw its store, with a
warning when the drawing feature has Clip Boxes off. See [Clip Boxes](../README.md#clip-boxes) for typical setups
and costs.

## Properties

| Property       | Description                                                                                                                    | Default          | Range           | Runtime change       |
|----------------|--------------------------------------------------------------------------------------------------------------------------------|------------------|-----------------|----------------------|
| Override Store | Light store to register the box in. Empty: the default store (`LightStore.Default`). Only lights in the same store can use it. | None             | Any Light Store | Re-registers the box |
| Sync Transform | On: the box follows its Transform every frame (dynamic). Off: it is static and moves only when `ApplyTransform()` is called.   | Off              |                 | Re-registers the box |
| Shape          | Center and size of the box in this Transform's space (rotation and scale apply).                                               | Center 0, size 1 | Any             | Immediate            |

## Scripting

| Member                                      | Description                                                                           |
|---------------------------------------------|---------------------------------------------------------------------------------------|
| `Shape`                                     | The box's center and size (`ClipBoxData`); setting it pushes the change to the store. |
| `OverrideStore`, `SyncTransform`            | As in the Inspector.                                                                  |
| `ApplyTransform()`                          | Copies the Transform's current matrix into the store (static boxes only).             |
| `TryGetRegistration(out store, out handle)` | The store and handle of the box while it is registered.                               |

Boxes without components, through the store: a room that keeps a lamp in, with a pillar cut out.

```C#
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

public static void ConfineToRoom(LightStore store, LightHandle lamp)
{
    var room = store.AddClipBox(
        new Placement { Position = new Vector3(0f, 2f, 0f), Rotation = Quaternion.identity },
        new ClipBoxData { Center = Vector3.zero, Size = new Vector3(6f, 4f, 6f) });

    var pillar = store.AddClipBox(
        new Placement { Position = new Vector3(1f, 2f, 1f), Rotation = Quaternion.identity },
        new ClipBoxData { Center = Vector3.zero, Size = new Vector3(0.5f, 4f, 0.5f) });

    store.SetClipBoxes(lamp, new[] { ClipBoxReference.Keep(room), ClipBoxReference.Subtract(pillar) });
}
```

## Notes

- **Same store.** A light uses a box only when both are in the same store; the light's Clip Boxes slot warns
  otherwise, and a warning is logged.
- **Disabled and duplicate boxes.** A disabled box (or one on an inactive GameObject) is ignored until it is enabled
  again. A box in two slots of one light counts once.
- **Scale and shear apply.** Unlike lights, a box follows its Transform's full matrix: rotation, scale, and even the
  shear a non-uniformly scaled parent introduces.
- **Dynamic box cost.** Dynamic boxes are re-packed every frame, and every box of the store is then copied to the GPU
  by a job: about 0.12 ms of main-thread time for 162 dynamic boxes on a Mali-G52 phone.
- **No shadows.** A Subtract box removes the glow inside it, not behind it.
