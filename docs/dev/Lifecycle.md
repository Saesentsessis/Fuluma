# Lifecycle callbacks in Unity 6000.5+

Unity 6000.5 added attribute-based lifecycle callbacks. They replace most uses of
`[InitializeOnLoad]`, `[RuntimeInitializeOnLoadMethod]` and `EditorApplication.playModeStateChanged`
for one job: **keeping static state correct when Enter Play Mode Options skip the domain reload and/or the scene reload.**

The order of events below was **measured** on Unity 6000.7.0b4 with a probe that logs every callback during an
edit → play → edit round trip (see "How this was measured" at the end). The API reference documents none of this ordering.

## The attributes

| Attribute | Namespace | Fires |
|---|---|---|
| `[OnExitingEditMode]` | `UnityEditor.Scripting.LifecycleManagement` | Editor is about to leave Edit mode |
| `[OnEnteringPlayMode]` | `UnityEngine` | Play mode is being entered |
| `[OnExitingPlayMode]` | `UnityEngine` | Play mode is being left |
| `[OnEnteringEditMode]` | `UnityEditor.Scripting.LifecycleManagement` | Editor enters Edit mode (also once at Editor startup) |
| `[OnCodeLoaded]` | `Unity.Scripting.LifecycleManagement` | After all assemblies load (startup or code reload) |
| `[OnCodeUnloading]` | `Unity.Scripting.LifecycleManagement` | Before assemblies unload for a code reload (reverse registration order) |
| `[OnCodeInitializing]` | `Unity.Scripting.LifecycleManagement` | After managed objects are restored following a domain reload |
| `[OnCodeDeinitializing]` | `Unity.Scripting.LifecycleManagement` | Before managed objects are backed up for a code reload (reverse registration order) |
| `[OnAssemblyLoaded]` / `[OnAssemblyUnloading]` | `Unity.Scripting.LifecycleManagement` | Per assembly |
| `[AutoStaticsCleanup]` | `Unity.Scripting.LifecycleManagement` | Resets static fields on entering/exiting Play mode when domain reload is disabled |
| `[NoAutoStaticsCleanup]` | `Unity.Scripting.LifecycleManagement` | Opts out of `[AutoStaticsCleanup]` |

Both statics-cleanup attributes are valid on class, struct, property, indexer, field and event declarations (checked
2026-11-19 by compiling against 6000.7.0b4: CS0592 on a method lists exactly these). Fuluma puts
`[NoAutoStaticsCleanup]` on whole types, which also satisfies the analyzer rule UAL0010.

There is **no "entered Play mode" attribute.** "Entering"/"Exiting" mean *during* the switch, not after it.
`EditorApplication.playModeStateChanged` with `EnteredPlayMode` / `EnteredEditMode` still works in 6000.7.

### Rules
- Methods must be `static`. Visibility doesn't matter (`private` is fine).
- The declaring class or struct **must be `partial`**: Unity's code generator adds the registration code to it.
  Otherwise compilation fails with `UAC0031: Lifecycle method '…' must be declared in a partial class or partial struct`.
- The `UnityEditor.*` attributes exist only in the Editor. In a runtime assembly, wrap them in `#if UNITY_EDITOR`.

## Measured order: edit → play → edit

Columns are the three Enter Play Mode Options configurations. "Same instance" means `OnDisable`/`OnEnable` run on the
same C# object with no new `Awake`. "New instance" means the object is destroyed and a fresh one is created (with `Awake`).

### Entering Play mode

| # | No domain reload, no scene reload (options 3) | No domain reload, scene reload (options 1) | Full reload (options 0) |
|---|---|---|---|
| 1 | `[OnExitingEditMode]` | `[OnExitingEditMode]` | `[OnExitingEditMode]` |
| 2 | `playModeStateChanged: ExitingEditMode` | same | same |
| 3 | `[InitializeOnEnterPlayMode]` | same | `OnDisable` (old object), *domain reload*, `[InitializeOnLoad]` |
| 4 | `OnDisable` (same instance) | `OnDisable` + `OnDestroy` (edit object) | `[InitializeOnEnterPlayMode]`, then a short-lived extra object: `OnEnable`, `OnDisable`, `OnDestroy` |
| 5 | `[OnEnteringPlayMode]` — `Application.isPlaying` is already **true** | same | same |
| 6 | `RuntimeInitialize`: `SubsystemRegistration` → `AfterAssembliesLoaded` → `BeforeSceneLoad` | same | same |
| 7 | `OnEnable` (same instance) | `Awake` + `OnEnable` (new instance) | `Awake` + `OnEnable` (new instance) |
| 8 | `RuntimeInitialize: AfterSceneLoad` | same | same |
| 9 | `Start` (next frame) | same | same |
| 10 | `playModeStateChanged: EnteredPlayMode` | same | same |

### Exiting Play mode (the same in all three configurations)

| # | Event |
|---|---|
| 1 | `playModeStateChanged: ExitingPlayMode` |
| 2 | `OnDisable` + `OnDestroy` on play-mode objects |
| 3 | `[OnExitingPlayMode]` |
| 4 | Edit scene restored: `Awake` + `OnEnable` (new instances), `Start` next frame |
| 5 | `[OnEnteringEditMode]` |
| 6 | `playModeStateChanged: EnteredEditMode` |

## What this means in practice

- **Objects are disabled and re-enabled on every switch, even with scene reload disabled.** Components that
  register in `OnEnable` and unregister in `OnDisable` survive switches without extra code. Code that relies only on
  `Awake` does not: with options 3 there is no new `Awake` on entering Play mode.
- **`[OnEnteringPlayMode]` runs in a quiet window.** Every scene object is disabled, nothing has been re-enabled yet,
  and `isPlaying` is already true. It's the right place to reset or release shared state for play mode.
  It is **not** a place to touch scene objects.
- **The first safe point after scene objects are back in Play mode** is `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`
  (after every `OnEnable`, before `Start`), or `playModeStateChanged: EnteredPlayMode` one frame later.
- **`[OnExitingPlayMode]` runs after play objects are gone and before the edit scene is back.** It's the place to
  release play-mode state.
- **`[OnEnteringEditMode]` runs after the edit scene is fully restored**, which makes it an "entered Edit mode" hook.
  It also runs once at Editor startup.
- **`[OnExitingEditMode]` runs before anything is disabled.** Scene objects are still enabled and registered at that point.
  It **also fires when the Editor quits**, not only on entering Play mode.
- **`[AutoStaticsCleanup]` resets every static of the class** unless the field is marked `[NoAutoStaticsCleanup]`.
  Statics that must outlive a switch (registries of live objects, id or epoch counters, cached singletons that
  survive with `HideFlags.DontSave`) must not be reset. A class whose statics should all persist simply shouldn't
  use the attribute.

## How Fuluma uses it

`Runtime/Core/FulumaLifecycle.cs` keeps fake lights alive across switches:

| | Unity 6000.5+ | Fallback (older Unity, or `FULUMA_LEGACY_LIFECYCLE` defined) |
|---|---|---|
| Leave a mode → release every `LightStore` | `[OnExitingEditMode]`, `[OnExitingPlayMode]` | `playModeStateChanged: ExitingEditMode / ExitingPlayMode` |
| Entered a mode → enabled lights re-register | `[OnEnteringEditMode]`, `RuntimeInitializeOnLoadMethod(AfterSceneLoad)` | `playModeStateChanged: EnteredEditMode / EnteredPlayMode` |
| Code reload → release every store | `[OnCodeUnloading]` | `ScriptableObject.OnDisable` during the domain unload |

Re-registering is a no-op for a light whose handle is still alive, so it doesn't matter that Unity's own
`OnDisable`/`OnEnable` cycle usually brings lights back first. The pass makes it guaranteed instead of
version-dependent. The light registry, the epoch counter and `LightStore.Default` are statics that are never reset.

Verified with one static and one dynamic `FakeSpotLight` in the probe scene, for both paths (the fallback forced with
`-define:FULUMA_LEGACY_LIFECYCLE` in a temporary `Assets/csc.rsp`), with options 3 and 1. Every store was empty right
after the mode was left, and both lights were back in their own pools by `EnteredPlayMode` and by `EnteredEditMode`.

## How this was measured

A temporary `[ExecuteAlways]` MonoBehaviour logged `Awake`/`OnEnable`/`Start`/`OnDisable`/`OnDestroy` with a managed
identity hash. Static methods logged every attribute above that relates to play mode, every
`RuntimeInitializeLoadType`, `[InitializeOnLoad]`, `[InitializeOnEnterPlayMode]` and `playModeStateChanged`.
An `-executeMethod` entry point set `EditorSettings.enterPlayModeOptions`, created a scene with the probe, entered
Play mode, exited after 5 frames and quit. It ran in batch mode (`-batchmode -nographics`) once per configuration.
The `OnCode*` / `OnAssembly*` attributes were **not** part of the probe; their order relative to the events above is unverified.
