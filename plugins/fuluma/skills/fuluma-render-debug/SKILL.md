---
name: fuluma-render-debug
description: Diagnoses Fuluma fake volumetric lights (FakeSpotLight, LinkedFakeLight, FakeLightClipBox, LightStore, FulumaRenderFeature in Unity URP) that don't show up, show up only somewhere, look wrong, behave differently in builds or on phones, or cost too much. Use it whenever a Unity project uses Fuluma (package com.saesentsessis.fuluma) and someone reports invisible or missing fake lights or volumetric beams, glow leaking through walls or cut off by the sky, lights too bright or too dim, clip boxes that do nothing, linked lights not following their Unity Light, "[Fuluma]" errors or warnings in the console or logcat, lights missing only in a player build or on Android/OpenGL ES, or a frame-rate drop after enabling MSAA or the light buffer, even if they don't name the component.
---

# Debugging Fuluma fake lights

Fuluma draws analytical fake volumetric spot lights through a URP renderer feature. Lights and clip boxes register
in a **light store**; each `FulumaRenderFeature` draws exactly one store, for the cameras and quality levels whose
renderer carries it. Almost every "my lights are missing" report is a break somewhere in that chain:

```
component (enabled, right type) → light store → render feature on the active renderer → camera filter → GPU/shader
```

Your job: find where the chain breaks, show the evidence, and propose the fix. **Diagnose first, then ask before
changing anything** (scenes, prefabs, render pipeline assets, project settings): these are the user's assets, a
"fix" to a shared renderer asset can change every scene, and the user may have reasons you can't see.

## Where things are

- **The package** in the user's project: `Packages/com.saesentsessis.fuluma/` when embedded, otherwise
  `Library/PackageCache/com.saesentsessis.fuluma@*/`. Its `README.md` and `Documentation~/` (one page per component
  and asset, `FulumaRenderFeature.md`, `Performance.md`) are the authority on settings and limits; read the relevant
  page before claiming how a setting behaves. Online: https://github.com/Saesentsessis/Fuluma
- **Project files are YAML text** you can read: scenes (`*.unity`), prefabs, the URP asset and its renderer assets
  (`*.asset`), `ProjectSettings/QualitySettings.asset`, `ProjectSettings/GraphicsSettings.asset`. Field names, enum
  values, script GUIDs and how to follow references: [references/unity-yaml.md](references/unity-yaml.md).
- **The Editor shows the answer for most setup problems.** Every light and clip box inspector has a status box at the
  top ("Registered in ... Drawn by: ...", or why not), and a clip box slot warns about boxes it ignores. When the user
  has the Editor open, ask them to read you that box: it checks the whole chain, including every quality level.
- **Logs:** Fuluma prefixes its messages with `[Fuluma]`. Editor: the Console or `Editor.log`. Android player:
  `adb logcat -s Unity`. Windows player: `Player.log`.

## Step 1: pin down the symptom

Ask (or infer from what the user said) before digging:

1. **Where** is it wrong: Scene view, Game view, a player build, a specific platform or device, a specific camera?
2. **What** is wrong: no fake lights at all, some lights missing, wrong look (where, how), or slow?
3. **Since when**: after adding MSAA, changing quality level, switching platform, editing a setting, entering play
   mode, a domain reload?
4. **Any `[Fuluma]` message** in the console or logs? Messages are the fastest route: check the table below first.

## Step 2: known messages

| Message (abridged) | Meaning | Fix to propose |
|---|---|---|
| "FulumaSpotLightRenderPass could not be initialized: it needs compute shaders and structured buffers (SSBOs) in vertex shaders." | The GPU/API has no structured buffers in the vertex stage. Typical: OpenGL ES 3.x on Mali (the driver allows 0 vertex storage blocks). Fuluma draws nothing there. | Use Vulkan on Android (Player Settings > Graphics APIs: Vulkan first, or Vulkan only). OpenGL ES support is planned, not available. |
| "The fake spot lights need a spot light shader (Hidden/Fuluma/Analytical/SpotLight); no lights are drawn." | The feature's Spot Light Shader field is empty (a build flow or script cleared it). | Assign the shader; the Editor refills empty fields when the feature is created, so re-adding the feature also works. |
| "The downsampled light buffer needs the shader 'Hidden/Fuluma/LightBuffer'; drawing at full resolution." | Light Buffer Shader field empty. | Assign it; until then lights draw at full resolution (slower, not missing). |
| "Fake spot lights need URP's depth texture here (...); enable 'Depth Texture' in the URP asset." | No depth input attachment on this platform, or MSAA with a light buffer: the lights need `_CameraDepthTexture`. | Enable Depth Texture on the URP asset, or turn MSAA off. |
| "More fake spot lights are visible than the render feature's maximum; the rest are skipped." | Max Spot Lights per camera exceeded (logged once). | Raise Max Spot Lights, or reduce visible lights. |
| "The spot light proxy descriptor is invalid (...); using the built-in proxy LODs." | The assigned Spot Light Proxy Array has invalid LODs. | Fix the asset (see `SpotLightProxyArrayDescriptor.md`). |
| "'<light>' uses clip box '<box>' from another light store; it is ignored." | Light and box are in different stores. | Give both the same Override Store. |

## Step 3: no fake lights at all

Walk the chain in this order; stop at the first break and report it.

1. **URP and RenderGraph.** Fuluma draws only in URP with RenderGraph. Check that the active quality level uses a URP
   asset (`QualitySettings.asset` per level, else `GraphicsSettings.asset`), and that URP's Compatibility Mode
   (render graph disabled) is off: the feature draws nothing there.
2. **The feature is on the renderer that is actually used.** The camera renders with its URP asset's renderer at the
   camera's renderer index (`m_RendererIndex`, -1 = the asset's default). That renderer must list a
   `FulumaRenderFeature` with `m_Active: 1`. **Each quality level** can use a different URP asset: a feature added to
   one renderer is invisible on the others. This is the most common cause when lights work on one machine or quality
   level and not another.
3. **Same store.** A component registers in its `overrideStore`, or the default store when that is empty. The feature
   draws its own `overrideStore`, or the default store when empty. Both empty, or both the same asset: fine. One set
   and the other empty, or two different assets: the lights exist but nobody draws them. Lights added from code go to
   the store the code passed.
4. **Camera filter.** The feature draws for a camera only if `camera.cullingMask & cameraLayers != 0` (Camera Layers
   on the feature; Everything by default). Preview cameras (inspector previews) never draw fake lights.
5. **GPU and shaders.** See the first two rows of the message table. On Android, a device or a build forced to OpenGL
   ES shows nothing and logs the SSBO message.
6. **Intensity.** Light Intensity on the feature, the light's Intensity and its color alpha (an extra 0–1 multiplier)
   all multiply; any of them at 0 makes lights invisible.

## Step 4: some lights missing

- **Not registered.** Component or GameObject disabled; a prefab asset (only instances in a scene register); a
  `LinkedFakeLight` whose Light is disabled or isn't a Spot light (only spot lights have a fake equivalent so far); a
  `LinkedFakeLight` on a GameObject without a Light. The inspector status box names the reason.
- **In another store** than the feature draws (Step 3.3), for example a prefab with its own Override Store.
- **Static light moved from code.** With Sync Transform off, a light keeps its registered placement until
  `ApplyTransform()` (or, from code, `SetSpotLightPlacement`) is called; moving the Transform alone does nothing in
  play mode or a player. Inspector and Scene view edits apply automatically.
- **Linked light set to Editor Only** (`syncMode: 1`): in play mode and players it ignores changes to its Light,
  including switching it on or off, until `Refresh()` is called.
- **Added from code after the frame's first render:** a dynamic light appears from the next frame (one frame late).
- **Lights added from code vanished after entering or leaving play mode, or a script reload:** stores release their
  memory then, and every handle issued before dies (`Contains` returns false). Components re-register by themselves; lights added
  through the `LightStore` API must be added again. With Enter Play Mode Options (domain reload off), static fields in
  the user's own scripts survive these switches: code that adds lights once behind a static flag, or keeps old
  handles in a static list, won't add them again. Add them on every entry into play mode (or check
  `store.Contains(handle)` and re-add).
- **Too many visible lights:** the Max Spot Lights warning in Step 2.
- **Clipped away by a clip box** (next section).

## Step 5: clip boxes don't behave

- **Clip Boxes fidelity feature off** on the feature (`fidelityFeatures` without the flag 2): boxes are ignored, lights glow
  everywhere. The light's inspector status warns about this per renderer.
- **Box in another store, or disabled** (or on an inactive GameObject): ignored; the slot shows a help box.
- **Keep vs Subtract.** A light with Keep boxes exists only inside the union of its Keep boxes: a Keep box that
  misses the beam makes the light disappear. A Subtract box removes the glow inside itself only.
- **No shadows.** Boxes don't cast shadows: subtracting a pillar removes the glow inside it, not behind it. Glow
  through a wall is expected without a box (fake lights don't know about geometry beyond the depth buffer); the fix
  is a Keep box around the room, which is what boxes are for.
- **Box placement.** A static box (Sync Transform off) also needs `ApplyTransform()` after moving it from code. Boxes
  follow their Transform's full matrix (scale and shear included), unlike lights.
- **Clip Box Piece Limit** (3 to 5): past the limit, further pieces of a ray merge into the last one and the gaps
  between them glow. Raise it for lights with several Subtract boxes.

## Step 6: wrong look

| Symptom | Likely cause | Check / fix |
|---|---|---|
| Glow disappears where the sky is behind it | Event set to After Rendering Opaques (`event: 300`): URP draws the skybox afterwards, over the glow | Event After Rendering Skybox (`400`, the default for new features) |
| Glow drawn over transparent objects in front of it, or transparents not covering it | Pass event relative to transparents | After Rendering Skybox: transparents blend over the glow; After Rendering Transparents: glow over them |
| Glow passes through all geometry | Scene Depth fidelity feature off (`fidelityFeatures` without the flag 1) | Turn Scene Depth on |
| Glow leaks through walls but stops at visible surfaces | Expected: no shadows | Keep box around the room |
| Hard cut through a light when the camera is inside or close to it | Near Plane Fade off, or its distance too small | Near Plane Fade on; Near Plane Fade Distance |
| Specks or holes near the axis of very wide cones (beyond about 160° full angle) | float precision | Wide Cone Precision fidelity feature on |
| Soft, blocky or haloed edges against geometry | Light Buffer Scale below 1; Bilinear upsample bleeds over silhouettes | Raise the scale, or Depth Aware upsample |
| Far too bright near the lamp, a glaring point | Lamp Glare = Point (`lampGlare: 1`) | None keeps the glow near the lamp finite |
| Beams faint from the side, very bright looking toward the lamp | Scattering Phase = Henyey-Greenstein (`scatteringPhase: 1`) | Expected for forward scattering; lower Scattering Anisotropy, or Isotropic. Above 0.8 the glow toward the lamp is less accurate |
| Linked lights much brighter or dimmer than expected | Distance Falloff and Intensity Multiplier | Range Scaled (default) is relative to the light's size; Unity falloff matches Unity Light units. Tune `intensityMultiplier` on `LinkedFakeLight` |
| Every light brighter or dimmer | Feature's Light Intensity | Global multiplier |
| Scaling a light's GameObject does nothing | Lights ignore scale | Use Range and the angles |
| Edits to a Spot Light Proxy Array asset don't show | The feature builds its proxy mesh when its pass is created | Clear and reassign the feature's Spot Light Descriptor |

## Step 7: different in a build or on a device

- **Shader variant stripping** (Project Settings > Fuluma, stored in `ProjectSettings/FulumaSettings.asset`) is the
  first suspect whenever a build looks different from the Editor (the Editor always has every variant). When a
  build lacks the variant a feature needs, Unity draws with the closest variant it kept (or, with Strict Shader
  Variant Matching in Player Settings, an error shader and a log line), so the lights can look wrong in a way that
  matches another setting (for example glaring like Lamp Glare = Point) rather than disappear. Three ways to get there:
  - **Custom mode** (`mode: 1`) keeps only the values ticked per keyword group: a `denied` entry with a value the
    project's features use (`_` is the "none" value of a group, i.e. the default for most of them) strips exactly the
    variants those features need. Compare every `denied` value and every `exclusions` entry with the features'
    settings (fidelity flags, scattering options). See [references/unity-yaml.md](references/unity-yaml.md).
  - **Exclusions** (both modes) strip every variant with all the picked values, even ones the features use.
  - **Settings changed from a script at runtime** (Scattering options, most fidelity features): Automatic mode keeps
    only what the features use at build time. Keep the runtime setups through a shader variant collection on that
    page, or Custom mode. Light Buffer Scale is safe to change at runtime.

  Evidence: each build logs `[Shader Stripping] ... kept N of M variants of <shader> (<mode>)` per shader and writes
  `Logs/ShaderStripping/<id>.json` listing what was kept and why.
- **OpenGL ES** (Android): not supported on drivers without vertex SSBOs (Step 2). Vulkan works.
- **Different quality level** in the build than in the Editor: Step 3.2.
- **Linked lights frozen in the build:** Editor Only sync mode (Step 4).

## Step 8: slow

Numbers for comparison: `Documentation~/Performance.md`.

- **MSAA with a downsampled light buffer** is the biggest trap on mobile: the buffer must read URP's depth copy, and
  on measured Mali phones a 0.5 buffer then costs about as much as, or more than, full resolution. It's also the usual
  answer to "the same scene got much slower after a settings change": check the URP asset's MSAA (`m_MSAA`) of every
  quality level the platform uses. Options: MSAA off for mobile levels, Light Buffer Timing Before Opaques, or full
  resolution. Measure.
- **Large Only** costs more than All on tile-based mobile GPUs (+16–25% measured): prefer All there.
- **Scene Depth off** usually costs a lot (occlusion savings are lost); it only pays off with a very small buffer.
- **Clip boxes** cost per light that uses them: one Keep box is nearly free, three boxes can add 50–80%.
- **Henyey-Greenstein** costs about +7–14%; Wide Cone Precision a few percent.
- **Light Buffer Scale** below about 0.25 saves little more: the buffer's fixed cost dominates.

## Report

Answer in this shape, so the user can act on it:

```
Cause: <the break in the chain, in one sentence>
Evidence: <what you read: file and field, inspector status text, log line>
Fix: <the change, where, and what else it affects (e.g. "this renderer is used by quality levels Low and Medium")>
Other findings: <anything else suspicious, or "none">
```

Then ask whether to apply the fix. If the evidence is not conclusive, say what would settle it (a field to check, a
screenshot of the inspector status, a log) instead of guessing.
