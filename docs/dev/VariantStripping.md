# Shader variant stripping

Status: implemented 2026-11-23 (design agreed with the user the same day, reviewed by a second model; its findings are
folded in below). Verified with an Android player build of the dev project: the spot light shader kept 48 of the 1728
variants that reached the stripper (12 per pass and graphics API), the light buffer 6 of 8.

## What the user gets

A **Project Settings > Fuluma** page with a **Shader Variant Stripping** section, modelled on Unity's Graphics > Fog
Modes. Settings live in `ProjectSettings/FulumaSettings.asset` (Editor only, versioned with the project).

- **Automatic** (default): keep what the build's Fuluma render features use, plus every variant listed in the shader
  variant collections on the page. Option **Count Inactive Features** (default off).
- **Custom**: one checkbox per value of every keyword group; a variant is kept if all its values are ticked. **Import
  from Automatic** ticks what Automatic would keep for the active build target.
- **Excluded combinations** (both modes, applied last): each entry picks a value or *Any* per group; every variant with
  all picked values is stripped. An entry only applies to passes and stages that declare every group it picks (so
  "near fade off + Henyey-Greenstein" doesn't strip the outside pass, which has no near fade group). An entry naming a
  value that no longer exists, or picking nothing, strips nothing.
- **Preview**: per shader, "keeps N of M variants" for the active build target, without building.
- **Log Summary** (one line per shader per build) and **Write Report** (`Logs/ShaderStripping/<source id>.json`, sorted,
  diffable).
- A runtime switch from a script to a stripped variant gets Unity's own behaviour. Play mode doesn't warn.

Stripping only affects player builds (and asset bundle builds, see Limits); the Editor always has every variant.

## Fuluma's Automatic rules (per feature, exact: a variant is kept if one feature allows all its keywords)

| Group | Kept |
|---|---|
| Depth source | Scene Depth off: `_DEPTH_NONE`. On: the depth texture and `_DEPTH_LOW_RES` (both paths, so Light Buffer Scale can change at runtime; user's choice), plus both input attachment variants on 6000.6+ (device support and the camera target's MSAA are only known at runtime). |
| Near fade (inside pass only) | the feature's flag |
| Clip class | Clip Boxes on: all six; off: none |
| Wide cones, phase, falloff, point lamp | the feature's settings |
| Light buffer `_FULUMA_CHECKERBOARD` | Depth Aware with scene depth: on; else off. Kept even at scale 1 (user's choice). |

Inactive features count only with Count Inactive Features. Features are those of every renderer of the URP assets the
build uses: the Quality levels active for the target, and Graphics settings' default when a level has none.

## Architecture

The framework moved to its own package on 2026-11-24, before 0.1.0 shipped:

- ShaderWorks (`com.saesentsessis.unity-shaderworks`, repository `Saesentsessis/unity-shaderworks`):
  `Editor/Stripping/` = `Saesentsessis.Shaders.Stripping.Editor` (namespace `Saesentsessis.Shaders.Stripping`),
  depends on UnityEditor, Burst, Collections and Mathematics only (no SRP). Tests in `Tests/Editor/Stripping/`
  (`Saesentsessis.Shaders.Stripping.Editor.Tests`), run in that repository's test projects.
- `Editor/Scripts/Build/`: Fuluma's side. `FulumaShaderKeywords` (schemas), `FulumaShaderStrippingSource`
  (Automatic rules, URP lookups), `FulumaProjectSettings` (`ScriptableSingleton`), `FulumaSettingsProvider` (the page).

Framework pieces:

- **Schema**: `ShaderKeywordSchema` (id, shader name, groups; former ids), `KeywordGroup` (one `multi_compile` line, up
  to 64 values, at most one without a keyword; former ids), `KeywordValue` (keyword or none; former keywords). Each value
  gets one bit of a 256-bit `VariantBits`; a group never crosses a 64-bit word.
- **Patterns**: a keep or exclusion pattern is a set of allowed bits; a variant matches when it is a subset. Groups a
  pass or stage doesn't declare contribute no bits (keep patterns accept them; exclusions listing them are dropped for
  that pass). `VariantMatching.IsKept` is the single test.
- **Decoding**: `PassLayout` per (subshader, pass, stage) from `ShaderUtil.GetPassKeywords(shader, pass, stage)`
  (documented on 6000.3 and 6000.7; no platform overload there), queried through `IKeywordQuery` structs
  (`KeywordSetQuery` for builds, `KeywordNameQuery` for tests). A variant with two values of a group on, or none on in a
  group without a no-keyword value, is kept and counted as unrecognized.
- **Rules**: `StrippingRules` per schema per build. Fails open (keeps everything, one warning) when the shader lacks a
  schema keyword, the source throws, a shader variant collection can't be read, or Automatic finds no pattern.
- **Build hook**: `ShaderVariantStripper` owns `IPreprocessShaders` (callback order 1000, after URP's) plus pre/post
  build callbacks. `StrippingSession` is created by the first processed shader and ended by the post-build callback or
  the Editor's next update (asset bundle and failed builds call no post-build callback); a new build first ends any
  leftover session. Kept entries are compacted forward and the tail cut once (back-to-front `RemoveAt` is quadratic).
- **Sources**: `IShaderStrippingSource` (stable id, display name, schemas, settings, `CollectAutomaticPatterns`), found
  through `TypeCache`, sorted by type name, one instance per build.
- **Settings**: `ShaderStrippingSettings` (serializable, `version`), stores values by keyword id, Custom as *denied*
  values (a value a package adds is kept by default), exclusions as group/value ids. Unknown ids are kept untouched.
  Drawn by `ShaderStrippingSettingsGui` (IMGUI, Undo on the owner object).
- **Shader variant collections**: read through `SerializedObject` (`m_Shaders` / `second.variants` / `keywords`; no
  public API enumerates them), pass types ignored, guarded (failure = keep everything).
- **Burst**: only `CountVariantsJob`, the preview's enumeration of every combination of a pass's groups, which grows
  with the product of group sizes. The build path is plain C#: its cost is Unity's per-variant `IsEnabled` queries,
  which Burst can't reach, and a synchronous Burst compile would cost more than every evaluation of a build.

## Tests

Framework (`Saesentsessis.Shaders.Stripping.Editor.Tests`, in ShaderWorks): bit layout and validation, `FromKeywords`, decoding and
unrecognized variants, keep and exclusion semantics including groups a pass lacks, settings surviving value reordering,
renames through former ids, JSON round trip, the Burst count against a managed enumeration.

Fuluma (`FulumaShaderStrippingTests`): each schema equals its shader's local keywords both ways (catches a keyword added
to a shader but not the schema), per-pass counts (1440 / 4 variants, one per pass with one value per group), and the
patterns a feature keeps.

## Limits and follow-ups

- Asset bundles / Addressables: changing stripping settings doesn't change a bundle's hash; rebuild bundles cleanly
  after changing them.
- Two packages embedding this framework would compile the same assembly name twice: extract it to its own package before
  a second consumer.
- Compute shaders (`IPreprocessComputeShaders`, kernels instead of passes) aren't covered yet; Fuluma's planned GPU
  culling shader will need it.
