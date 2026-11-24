using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// After culling, reorders each visible-light region of one pool by clip class, so each class draws with the shader
	/// variant made for it, in this order: no clip boxes, one box, one keep and one subtract box, two keep boxes, two
	/// subtract boxes, then the rest (three or four boxes). One iteration per bucket; the order within a class is
	/// arbitrary (blending is additive).
	/// </summary>
	[BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = true)]
	internal unsafe struct SpotLightClipClassJob : IJobParallelFor
	{
		public const int NoBoxes = 0;
		public const int OneBox = 1;
		public const int KeepMinusSubtract = 2;
		public const int TwoKeeps = 3;
		public const int TwoSubtracts = 4;
		public const int General = 5;
		public const int ClassCount = 6;

		/// <summary>
		/// The clip class (<see cref="NoBoxes"/> to <see cref="General"/>) of a light with these references. References are
		/// packed from slot 0 on (removals compact them), so an unused slot ends the list.
		/// </summary>
		internal static int ClipClass(in PackedClipBoxReferences references)
		{
			var first = references.Slots01 & 0xFFFFu;
			var second = references.Slots01 >> 16;

			if (first == PackedClipBoxReferences.None)
				return NoBoxes;

			if (second == PackedClipBoxReferences.None)
				return OneBox;

			if ((references.Slots23 & 0xFFFFu) != PackedClipBoxReferences.None)
				return General;

			var subtracts = ((first & PackedClipBoxReferences.SubtractBit) != 0 ? 1 : 0)
			                + ((second & PackedClipBoxReferences.SubtractBit) != 0 ? 1 : 0);
			return subtracts == 0 ? TwoKeeps : subtracts == 1 ? KeepMinusSubtract : TwoSubtracts;
		}

		/// <summary>
		/// The pool's references, index-aligned with its lights.
		/// </summary>
		[ReadOnly] public NativeArray<PackedClipBoxReferences> References;

		/// <summary>
		/// Shared with the other pool's jobs; each iteration touches only its own region.
		/// </summary>
		[NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction]
		public NativeArray<uint> Indices;

		/// <summary>
		/// Visible lights per slot, written by the culling job this one waits for.
		/// </summary>
		[ReadOnly, NativeDisableContainerSafetyRestriction]
		public NativeArray<int> Counters;

		/// <summary>
		/// Region start per slot in <see cref="Indices"/>.
		/// </summary>
		[ReadOnly] public NativeArray<int> RegionStarts;

		/// <summary>
		/// Per slot: lights per class (the general class is the rest of the region).
		/// </summary>
		[NativeDisableParallelForRestriction, NativeDisableContainerSafetyRestriction]
		public NativeArray<SpotLightClipClassCounts> ClassCounts;

		/// <summary>
		/// Slot of this pool's bucket 0.
		/// </summary>
		public int SlotStart;

		public void Execute(int bucket)
		{
			var slot = SlotStart + bucket;
			var start = RegionStarts[slot];
			var count = Counters[slot];

			// In-place counting sort (American flag sort): count the classes, give each its range, then swap every light
			// into its class's range.
			var counts = stackalloc int[ClassCount];
			var next = stackalloc int[ClassCount];
			var end = stackalloc int[ClassCount];

			for (var clipClass = 0; clipClass < ClassCount; clipClass++)
				counts[clipClass] = 0;

			for (var i = start; i < start + count; i++)
				counts[ClipClass(References[(int)Indices[i]])]++;

			var offset = start;
			for (var clipClass = 0; clipClass < ClassCount; clipClass++)
			{
				next[clipClass] = offset;
				offset += counts[clipClass];
				end[clipClass] = offset;
			}

			// Once every class but the last holds only its own lights, the last does too.
			for (var clipClass = 0; clipClass < ClassCount - 1; clipClass++)
			{
				while (next[clipClass] < end[clipClass])
				{
					var index = Indices[next[clipClass]];
					var target = ClipClass(References[(int)index]);

					if (target == clipClass)
					{
						next[clipClass]++;
						continue;
					}

					// Move it to its class's range; whatever comes back is checked in the next round.
					Indices[next[clipClass]] = Indices[next[target]];
					Indices[next[target]++] = index;
				}
			}

			var result = default(SpotLightClipClassCounts);
			for (var clipClass = 0; clipClass < SpotLightClipClassCounts.StoredCount; clipClass++)
				result[clipClass] = counts[clipClass];

			ClassCounts[slot] = result;
		}
	}
}
