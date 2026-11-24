# Fake Light (Linked)

Component: `LinkedFakeLight` (**GameObject > Light > Volumetric > Fake Light (Linked)**, which creates a spot `Light` with it, or **Add Component > Light > Volumetric > Fake Light (Linked)** on a GameObject with a `Light`)

A fake volumetric light that follows a Unity `Light` on the same GameObject: its color, color temperature,
intensity, range and spot angles. Spot lights only for now.

<!-- Image: a Unity spot Light with Fake Light (Linked), its glow in the Scene view, and both inspectors. -->

## Overview

The component reads the `Light` on its GameObject and keeps a matching fake spot light in its store:

- **Color** from the Light's color, with its color temperature applied when the project and the Light use color
  temperature.
- **Intensity** is the Light's intensity times **Intensity Multiplier**, so a beam's brightness isn't tied to how
  brightly the Light lights surfaces.
- **Range**, **spot angle** and **inner spot angle** from the Light.

The fake light exists only while the `Light` component is enabled and of type Spot: switching the Light off, or to
another type, removes it, and switching it back adds it again.

The Unity Light keeps lighting surfaces, and costing, as usual. For glow without surface lighting, use
[Fake Spot Light](FakeSpotLight.md) instead.

## Properties

| Property             | Description                                                                                                                                                                                                                                                                                                                           | Default     | Range                    | Runtime change                    |
|----------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------|--------------------------|-----------------------------------|
| Sync Mode            | Every Frame: the Light's values are checked every frame, right before the upload, and only changes are pushed (a few native property reads per light and frame). Editor Only: checked every frame in the Editor outside play mode; in play mode and in players only on enable and when `Refresh()` is called, with no per-frame cost. | Every Frame | Every Frame, Editor Only | Immediate                         |
| Intensity Multiplier | The fake light's intensity is the Light's intensity times this.                                                                                                                                                                                                                                                                       | 1           | ≥ 0                      | Immediate                         |
| Override Store       | Light store to register the light in. Empty: the default store (`LightStore.Default`).                                                                                                                                                                                                                                                | None        | Any Light Store          | Re-registers the light            |
| Sync Transform       | On: the light follows its Transform every frame (dynamic). Off: it is static and moves only when `ApplyTransform()` is called.                                                                                                                                                                                                        | Off         |                          | Re-registers the light (new pool) |
| Clip Boxes           | Four slots, each a clip box and its role (Keep or Subtract), as on [Fake Spot Light](FakeSpotLight.md).                                                                                                                                                                                                                               | All unused  | 4 slots                  | Immediate                         |

## Scripting

| Member                                                                           | Description                                                                                                                                                                            |
|----------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Light`                                                                          | The Unity `Light` this component follows.                                                                                                                                              |
| `HasFakeLight`                                                                   | Whether the Light currently has a fake light (enabled, and a spot light).                                                                                                              |
| `Refresh()`                                                                      | Copies the Light's current values now; adds or removes the fake light if the Light was switched on or off or changed type. Needed with Editor Only after changing the Light from code. |
| `SyncMode`, `IntensityMultiplier`                                                | As in the Inspector.                                                                                                                                                                   |
| `OverrideStore`, `SyncTransform`, `ClipBoxes`, `ApplyTransform()`, `ApplyData()` | As on [Fake Spot Light](FakeSpotLight.md).                                                                                                                                             |

Changing a linked Light from code in Editor Only mode:

```C#
using Saesentsessis.Fuluma.Rendering.Components;
using UnityEngine;

public static void SetLampColor(LinkedFakeLight linked, Color color)
{
    linked.Light.color = color;
    linked.Refresh(); // Editor Only doesn't poll in play mode; Every Frame would pick this up by itself.
}
```

## Notes

- **Spot lights only, and only while the Light is on.** Point, directional and area Lights have no fake light yet.
- **Editor Only and runtime changes.** In play mode and in players, an Editor Only light ignores changes to its
  Light, including switching it on or off, until `Refresh()` is called.
- **Static by default.** With Sync Transform off, moving the GameObject from code needs `ApplyTransform()`.
- **Scale is ignored**, as for every light: position and rotation only.
- **Prefab assets** don't register; their instances do, once enabled in a scene.
