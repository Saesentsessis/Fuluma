# Technical Deep Dive

How Fuluma works under the hood, explained conceptually, together with why it is built this way and what was tried
and rejected along the way. No formulas; the costs quoted come from the package's own measurements (see
[Performance](Performance.md)).

## 1. The Light Model and the Integral

A fake spot light is a cone of light-scattering air. Looking through it, a pixel receives the light scattered toward
the camera at every point along its view ray inside the cone, up to where the ray hits scene geometry. Fuluma
computes that sum, the in-scattering integral, for every pixel the light covers.

The light at each point of the cone is shaped by two things:

- **Distance from the apex.** Brightness falls off with the inverse square of the distance, like a real light, and
  is smoothly forced to zero at the light's range, so the volume ends without a hard edge. Very close to the apex the
  falloff is softened slightly, so the glow doesn't become infinitely bright.
- **Angle from the axis.** Inside the inner angle the light is at full strength; between the inner and the outer
  angle it fades smoothly to zero (the penumbra); outside the outer angle there is none.

**Why analytical instead of raymarching.** A raymarcher samples the integral at many steps along every ray, per
light, so its cost grows with covered pixels, step count and overlapping lights, and it needs noise and denoising to
hide its banding. Fuluma instead finds exactly where each view ray enters and leaves the cone (and its rounded cap,
and the scene depth) and integrates the glow over that interval in closed steps: the ray is split where it crosses
the inner cone into at most three segments, and each segment is integrated with a three-point Gauss-Legendre
quadrature over the angle at which each point of the segment is seen from the light's apex. A handful of
evaluations per pixel replace dozens of raymarch steps, with no noise and nothing to blur. Rendered results match a
brute-force numerical integral within about 1-2%.

**What was tried and rejected.**

- *A closed-form answer for the penumbra.* The exact antiderivative exists, but evaluating it in 32-bit floats loses
  most of its digits to cancellation; the quadrature is both stable and cheap.
- *One angular sample per segment.* Cheaper, but it leaves visible seams where segments meet.
- *Trigonometry-free sample placement.* About 2% faster, but less accurate; archived in the development project.
- *Half precision* for the angular falloff, the intersections or depth: the angular falloff multiplies its input's
  error by up to thousands for narrow penumbras, so these stay in full precision.

**Very wide cones.** Past an outer half-angle of about 80 degrees, the standard way of intersecting a ray with a cone
subtracts two nearly equal large numbers and loses precision near the axis, which shows as holes and speckles. The
optional **Wide Cone Precision** fidelity feature switches to a rearranged form without that cancellation, for a few
percent of GPU time; it is off by default because most cones never need it.

## 2. Proxy Meshes, Passes and Culling

**Proxy meshes.** The GPU needs geometry to know which pixels a light may cover, so each light is drawn as a proxy:
a cone with a rounded cap, generated once per LOD. A proxy with few vertices is a polygon, not a circle, so the
vertex shader widens it just enough that it always encloses the true cone, and the pixel shader then computes the
exact glow, discarding the pixels that miss the volume. Coarser LODs therefore cost fewer vertices but cover more
pixels that do nothing; the LOD is picked per light from its size on screen, so large lights get tight proxies and
distant ones cheap proxies.

**Two passes.** When the camera is outside a light's proxy, the proxy's front faces are drawn with the depth test
on, so lights hidden behind geometry are rejected by the GPU before any shading (early depth rejection). When the
camera is inside or very near a proxy, its front faces may be behind the camera or cut by the near plane, so those
lights are drawn by their back faces with the depth test off, and the shader works out the visible part of the ray
itself. Which pass a light takes is decided per light by culling, against that light's own LOD shape: an early
version tested against a shape enclosing every LOD, and the coarsest LOD's inflation pushed nearly every light into
the slower inside pass. The **near-plane fade** belongs to the inside pass, which is why culling moves lights into it
slightly before any of their pixels could reach the fade zone; otherwise the fade would pop in at the switch.

**Culling.** Each camera's culling runs on worker threads as a Burst job, scheduled right when URP starts culling the
camera so the two overlap. Every light's bounding sphere is tested against the six frustum planes; only spheres that
straddle a plane get the exact test of the cone with its cap. Surviving lights are sorted into buckets by pass and
LOD (and, with Large Only, by whether they go through the light buffer), and each bucket becomes one instanced draw:
one draw per pool, pass and LOD, however many lights. With 65,536 static lights this takes well under a tenth of a
millisecond with worker threads on a desktop CPU. GPU culling is planned; until then every light is culled on the
CPU.

## 3. The Light Store and the GPU Mirror

**Pools.** A light store keeps each light type in two pools: static lights (placed from code, moved only on request)
and dynamic lights (re-read from their Transforms every frame). Each pool stores its lights densely, as parallel
arrays (packed transforms, packed light data, culling data, clip box references) at the same index, so the GPU reads
them without indirection. Removing a light moves the last light into its place, which keeps the arrays dense; the
light's handle stays valid through all of this, because it refers to a stable slot rather than to the dense index.

**Handles and epochs.** Every allocation of a store's memory gets a new, globally unique epoch, and every handle
records the epoch it was issued under. When the store is released (on a play-mode switch in the Editor, on a code
reload, or when the store goes away), the epoch changes and every old handle simply stops validating, instead of
pointing at a reused slot.

**The GPU mirror.** Each store has one GPU copy, shared by every render pass that draws it. Static data is uploaded
only where it changed: the store records, per pool, the range of indices modified since the last upload. Dynamic
transforms change every frame, so a job copies the Transforms into the store and a second job copies them straight
into a GPU buffer locked for writing, off the main thread. Those per-frame buffers rotate (three by default, see
**Dynamic Buffer Count**), so the CPU never writes a buffer the GPU may still be reading from an earlier frame.

**Jobs and safety.** Culling jobs read the pools while the main thread may want to change them, so the store tracks
every job reading it and waits for them before any change. That is why the store's API is main-thread only.

**Per-draw cost.** On phones, the CPU cost of each instanced draw was dominated by the size of its per-draw property
block, so everything that is the same for a whole camera (the buffers, the sample count) is bound on the materials
once per camera, and each draw carries only its offset and proxy shape.

## 4. The Light Buffer and Clip Boxes

**The light buffer.** The glow is smooth, so it can be shaded at a fraction of the screen resolution and upscaled
with little visible loss, except where it meets geometry. Shading into a smaller buffer needs a matching depth
buffer, so the scene depth is first reduced to the buffer's size: each low-resolution texel keeps the farthest depth
of the pixels it covers, or, for depth-aware upscaling, alternates farthest and nearest in a checkerboard so both
sides of an edge are represented. The lights are drawn into the buffer with early depth rejection against that
reduced depth, then composited onto the camera: bilinearly, or depth-aware, where texels whose depth differs from
the pixel's are weighted down so glow doesn't bleed over nearer geometry. Where all four neighbours agree in depth,
the depth-aware composite falls back to a single bilinear read.

**Tile-based GPUs.** Mobile GPUs keep the frame in on-chip tiles and lose time whenever a render pass must store
color and depth to memory and load them back. A buffer shaded after the opaques splits URP's single opaque-plus-
transparent pass. Shading it *before* the opaques, from URP's depth prepass, leaves only the composite after them,
so URP's passes stay merged; that saves 2-7% of the frame on Mali, but only when a depth prepass exists anyway. Auto
timing uses it only when the renderer's own settings already produce a prepass. It can't see prepasses that other
features add, because detecting them would need reflection into URP's internals, which the package avoids.

**Rejected: variable-rate shading.** Shading the lights at a coarser rate instead of into a buffer was measured and
was slower (22.5 ms against 16.8 ms for a half-resolution buffer in a 4K stress frame): the cost was in blending,
which coarser shading doesn't reduce.

**Clip boxes.** A clip box cuts the view ray rather than the light: transformed into the box's own space, the ray
crosses the box in one interval, found with the standard slab test. A light's Keep boxes give intervals where it may
glow, its Subtract boxes intervals where it may not; combining them leaves up to a few separate pieces of the ray
inside the cone, and each piece is integrated with the same quadrature as an unclipped ray. Boxes therefore never
darken anything behind them: they limit where light exists, they don't cast shadows.

**Clip classes.** A single shader handling any four boxes made every clipped light pay for the most general case: in
the first measurement, lights with two boxes cost over five times as much as lights without boxes on a Mali-G52 at
full resolution. Instead, culling sorts the visible lights
by what their boxes need (no boxes, one box, a room with a cut-out, two rooms, two cut-outs, three or four boxes) and
each class is drawn with a shader variant specialized for it. Box transforms are moved out of the pixel shader into
the vertex shader, packed tightly enough to fit the 15 interpolator rows that OpenGL ES devices guarantee. Lights
without boxes keep using the plain shader, so boxes cost only on the lights that use them.

**What's next.** The analytical model is single-scattering for one cone shape today; more light models, point
lights, more shaping and clipping features, and GPU culling are on the [roadmap](../README.md#upcoming-changes).
