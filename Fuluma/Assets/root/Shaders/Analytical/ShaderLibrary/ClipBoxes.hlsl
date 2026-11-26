#ifndef FULUMA_CLIP_BOXES_INCLUDED
#define FULUMA_CLIP_BOXES_INCLUDED

// Clip boxes (LightStore.ClipBoxes.cs): oriented boxes limiting where a light glows. A light references up to 4 boxes of
// its store; its region is the union of its Keep boxes (everything when it has none) minus the union of its Subtract
// boxes. Along a view ray that region is a few intervals ("pieces"), each integrated like the whole ray used to be.
//
// Layouts must match Runtime/Data/PackedClipBoxData.cs (48 B: world-to-box rows mapping the box onto [-1, 1]^3) and
// Runtime/Data/PackedClipBoxReferences.cs (8 B: four 16-bit references, 15-bit box index + Subtract bit, 0xFFFF unused).
//
// Kept light on registers on purpose: the four box intervals live in two float4 (no arrays), and the pieces are walked
// breakpoint by breakpoint (FulumaClipNextBreakpoint, FulumaClipInside) and integrated as they close, instead of sorting
// all endpoints and storing the pieces. A sorting network plus stored pieces raised the shader's register use enough to
// slow every pixel of the variant (+60% for one-box lights on a Mali-G52).

#define FULUMA_CLIP_BOX_SLOTS 4
#define FULUMA_MAX_CLIP_PIECES 5
#define FULUMA_CLIP_BOX_NONE 0xFFFFu
#define FULUMA_CLIP_BOX_SUBTRACT 0x8000u
#define FULUMA_CLIP_BOX_INDEX_MASK 0x7FFFu

// Smallest box-space direction component the orthographic view ray gets (FulumaClipBoxVaryings): its slab distances
// stay finite (at most ~2e20), where an exact zero would divide by zero.
#define FULUMA_CLIP_MIN_DIRECTION 1e-20

struct FulumaPackedClipBox
{
    float4 row0;
    float4 row1;
    float4 row2;
};

// A light's boxes along one ray: per slot, the box's interval clamped to the ray's [near, far] (unused slots and missed
// boxes collapse onto far), and each slot's role: +1 keep, -1 subtract, 0 unused (one float4 for both: fewer registers).
struct FulumaClipIntervals
{
    float4 starts;
    float4 ends;
    float4 roles;
    bool anyKeep;   // the light has keep boxes: outside all of them is outside the region
};

uint FulumaClipBoxReference(uint2 references, uint slot)
{
    return ((slot < 2 ? references.x : references.y) >> ((slot & 1) * 16)) & 0xFFFFu;
}

// [enter, exit] of the ray o + t * d inside the box [-1, 1]^3, with o and d already in box space (enter >= exit: the ray
// misses it). A zero component of d divides by zero, which only Direct3D defines (+-inf: the slab is everything or
// nothing along the ray); the orthographic ray, constant per light and exactly axis-aligned in common views, is kept
// off zero (FULUMA_CLIP_MIN_DIRECTION). d = 0 entirely (no box) only occurs for unused slots, whose result is ignored.
float2 FulumaClipBoxSlab(float3 o, float3 d)
{
    float3 inverse = rcp(d);
    float3 t0 = (-1.0 - o) * inverse;
    float3 t1 = (1.0 - o) * inverse;
    float3 tNear = min(t0, t1);
    float3 tFar = max(t0, t1);
    return float2(max(max(tNear.x, tNear.y), tNear.z), min(min(tFar.x, tFar.y), tFar.z));
}

// The pieces of [near, far] (the light's volume along the ray, clipped at the scene depth) in one box's region, from the
// box's interval along the ray. Keep: the interval (second piece empty). Subtract: before and after it (a missed box
// leaves the whole ray). An empty piece has end <= start. Returns false when nothing is left.
bool FulumaClipSingleBoxPieces(float2 interval, bool subtract, float near, float far,
    out float2 firstPiece, out float2 secondPiece)
{
    float start = clamp(interval.x, near, far);
    float end = clamp(interval.y, near, far);
    bool hit = end > start;

    if (subtract == false)
    {
        firstPiece = float2(start, end);
        secondPiece = float2(far, far);
        return hit;
    }

    start = hit ? start : far;
    end = hit ? end : far;
    firstPiece = float2(near, start);
    secondPiece = float2(end, far);
    return start > near || far > end;
}

// Whether the room's (keep box's) interval misses [near, far]: then a keep-minus-subtract light has nothing left, and
// callers leave before computing the cut-out's interval.
bool FulumaClipRoomMissed(float2 room, float near, float far)
{
    return clamp(room.y, near, far) <= clamp(room.x, near, far);
}

// Keep minus subtract from the two intervals: the room's interval minus the cut-out's, at most two pieces (an empty
// piece has end <= start). Returns false when nothing is left.
bool FulumaClipKeepMinusSubtractPieces(float2 room, float2 cut, float near, float far,
    out float2 firstPiece, out float2 secondPiece)
{
    float roomStart = clamp(room.x, near, far);
    float roomEnd = clamp(room.y, near, far);
    float cutStart = clamp(cut.x, near, far);
    float cutEnd = clamp(cut.y, near, far);

    // A missed cut-out collapses onto far: the first piece is then the whole room, the second empty.
    bool hit = cutEnd > cutStart;
    cutStart = hit ? cutStart : far;
    cutEnd = hit ? cutEnd : far;

    firstPiece = float2(roomStart, min(roomEnd, cutStart));
    secondPiece = float2(max(roomStart, cutEnd), roomEnd);
    return firstPiece.y > firstPiece.x || secondPiece.y > secondPiece.x;
}

// Two keeps from the two intervals: their union, one piece when they overlap, else two in order (an empty piece has
// end <= start). Returns false when the ray misses both.
bool FulumaClipKeepUnionPieces(float2 a, float2 b, float near, float far, out float2 firstPiece, out float2 secondPiece)
{
    a = clamp(a, near, far);
    b = clamp(b, near, far);

    // A missed box collapses onto far (zero length, after every other interval).
    a = a.y > a.x ? a : float2(far, far);
    b = b.y > b.x ? b : float2(far, far);

    bool aFirst = a.x <= b.x;
    float2 low = aFirst ? a : b;
    float2 high = aFirst ? b : a;
    bool overlap = high.x <= low.y;

    firstPiece = overlap ? float2(low.x, max(low.y, high.y)) : low;
    secondPiece = overlap ? float2(far, far) : high;
    return firstPiece.y > firstPiece.x || secondPiece.y > secondPiece.x;
}

// Two subtracts from the two intervals: the ray minus their union, at most three pieces in order (an empty piece has
// end <= start). Returns false when the boxes cover all of [near, far].
bool FulumaClipExcludeTwoPieces(float2 a, float2 b, float near, float far,
    out float2 firstPiece, out float2 secondPiece, out float2 thirdPiece)
{
    a = clamp(a, near, far);
    b = clamp(b, near, far);

    // A missed box collapses onto far (zero length, after every other interval).
    a = a.y > a.x ? a : float2(far, far);
    b = b.y > b.x ? b : float2(far, far);

    bool aFirst = a.x <= b.x;
    float2 low = aFirst ? a : b;
    float2 high = aFirst ? b : a;
    bool overlap = high.x <= low.y;

    // Before the first box, between the boxes (when they don't overlap), after the last one.
    firstPiece = float2(near, low.x);
    secondPiece = overlap ? float2(far, far) : float2(low.y, high.x);
    thirdPiece = float2(overlap ? max(low.y, high.y) : high.y, far);
    return firstPiece.y > firstPiece.x || secondPiece.y > secondPiece.x || thirdPiece.y > thirdPiece.x;
}

// The light's box intervals along the ray, from each slot's slab result (FulumaClipBoxSlab: enters, exits), clamped to
// [near, far] (the light's volume, clipped at the scene depth). references: the light's packed references, for each
// slot's role; unused slots (and missed boxes) collapse onto far. All unused (a light without boxes) gives a region that
// is the whole ray.
FulumaClipIntervals FulumaClipIntervalsFromSlots(uint2 references, float4 enters, float4 exits, float near, float far)
{
    float4 starts = clamp(enters, near, far);
    float4 ends = clamp(exits, near, far);

    FulumaClipIntervals clip;
    UNITY_UNROLL
    for (uint slot = 0; slot < FULUMA_CLIP_BOX_SLOTS; slot++)
    {
        uint reference = FulumaClipBoxReference(references, slot);
        bool used = reference != FULUMA_CLIP_BOX_NONE;
        bool hit = used && ends[slot] > starts[slot];

        clip.starts[slot] = hit ? starts[slot] : far;
        clip.ends[slot] = hit ? ends[slot] : far;
        clip.roles[slot] = used ? ((reference & FULUMA_CLIP_BOX_SUBTRACT) == 0 ? 1.0 : -1.0) : 0.0;
    }

    clip.anyKeep = any(clip.roles > 0.0);
    return clip;
}

// Whether the ray has any part in the region at all: false when every keep box misses it (subtract boxes are left to
// the walk).
bool FulumaClipAnyInside(FulumaClipIntervals clip)
{
    return clip.anyKeep == false || dot(max(clip.ends - clip.starts, 0.0), saturate(clip.roles)) > 0.0;
}

// Smallest slot start or end after t (far when none): the next point where the region can change.
float FulumaClipNextBreakpoint(FulumaClipIntervals clip, float t, float far)
{
    float4 starts = clip.starts > t ? clip.starts : far;
    float4 ends = clip.ends > t ? clip.ends : far;
    float4 next = min(starts, ends);
    return min(min(next.x, next.y), min(next.z, next.w));
}

// Whether t, strictly between two breakpoints, is inside the region: in a keep box (or the light has none) and in no
// subtract box. Collapsed slots (start = end = far) never contain it, since t < far.
bool FulumaClipInside(FulumaClipIntervals clip, float t)
{
    float4 within = step(clip.starts, t) * step(t, clip.ends);
    bool inKeep = clip.anyKeep == false || dot(within, saturate(clip.roles)) > 0.0;
    bool inSubtract = dot(within, saturate(-clip.roles)) > 0.0;
    return inKeep && inSubtract == false;
}

#endif // FULUMA_CLIP_BOXES_INCLUDED
