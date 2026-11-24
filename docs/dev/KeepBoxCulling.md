# Culling bounds from keep boxes (deferred)

Status: designed, not implemented (user's decision, 2026-11-19). This note keeps what the design found so the work can
resume without redoing it.

## Idea and expected gain

A light with keep boxes glows only inside their union, so it can be culled when none of its keep boxes is in the
camera's view, even if its cone is. Gains:

- **Fewer draws and vertices** for lights in rooms out of view (a building's other floors and rooms). Each culled light
  saves its proxy's vertex work and, where it would have reached the screen, the fragment shader's early-out pixels.
- **Less culling output to sort and upload** (index buffer, clip class grouping).
- **No pixel savings** for lights that stay visible: their proxy still covers the whole cone (see "Not covered").

How big this is depends on content: large for interiors where most rooms are off-screen, near zero for open scenes.

## Issues to handle

1. **Box bounds on the CPU.** The store keeps placements and the packed world-to-box rows the GPU needs; culling needs
   each box's world center and three half-axes. Add a per-box world bound (48 B, CPU only, allocated with the box array:
   no memory without boxes). Static boxes update it on add / shape / transform changes; dynamic boxes in
   `PackDynamicClipBoxesJob`. Scaled and sheared placements are parallelepipeds: the plane test is exact for them.
2. **Thread safety.** Box edits (`AddClipBox`, `Remove(ClipBoxHandle)`, `SetClipBoxShape`, `SetClipBoxPlacement`,
   `ClearClipBoxes`) only complete the dynamic box sync today, since culling never reads boxes. Once it does, they must
   also `CompleteSpotLightReaders()` (the box array can grow and move). Dynamic boxes are already packed before culling
   (the mirror completes `ScheduleDynamicClipBoxSync` in `beginContextRendering`).
3. **Deferred removal.** A light may still reference a removed box until the references are flushed. Culling must only
   read references through the flushing accessor (`GetSpotLightClipBoxReferences`), as the grouping job already does.
4. **Clip boxes off.** With the feature's `useClipBoxes` off (or no boxes uploaded) the shader ignores boxes, so culling
   must too, or lights disappear while clipping is off. Tie it to the same condition as the clip class grouping
   (`PartitionedByClipClass`).
5. **Stay conservative.** Never drop a light the shader draws. Only keep boxes shrink the region (subtract boxes are
   ignored); a degenerate keep box (`PackedClipBoxData.Empty`) makes the shader draw nothing, so culling it is
   consistent. Test with randomized scenes comparing the culled set with rendered glow.
6. **Budget (0.25 ms).** The 65536-light benchmark has no boxes and must not get slower. Each boxed light adds a random
   access to its references (8 B) and up to four bounds (48 B each) plus a six-plane test per keep box.
7. **Where.** Recommended: in `SpotLightClipClassJob` after culling, which already visits only visible lights with
   boxes and reads their references; it would drop lights whose keep boxes are all off-screen and shorten each region
   (the culler then reads the new counts back). Zero cost without boxes and the tuned culling job stays untouched.
   Alternative: inside `SpotLightCullingJob` / `SpotLightCullingFastJob` (earlier rejection, but both jobs and their
   assembly change).
8. **What the bound feeds.** Start with visibility only. LOD from the clipped bound is visually safe (the proxy always
   encloses the cone; LOD only changes overdraw) but changes the `LargeOnly` classification. The inside / outside pass
   decision must keep using the full cone's proxy.
9. **Stats and tests.** Visible counts, per-LOD counts and draw stats drop for boxed lights; the stress test needs a
   scenario with many rooms out of view.

Open questions from the design (not answered yet): visibility only or also LOD / `LargeOnly`; grouping job or culling
job; exact box test or a sphere per box (recommended: exact, at most four boxes); whether a light entirely inside a
subtract box should be culled (recommended: no, rare).

## Not covered

- **Proxy overdraw:** a visible light in a small room still rasterizes its whole cone proxy, and pixels outside the room
  run the fragment shader up to its early exit. Fixing that needs a different proxy (e.g. limited to the keep box), a
  separate step.
- **Occlusion:** only the camera frustum; rooms behind walls still reach the GPU, where the depth test rejects them.
