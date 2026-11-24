using Saesentsessis.Fuluma.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Core.Pools
{
	/// <summary>
	/// Drops every reference to the removed clip boxes from one pool's lights, so a later box reusing an index can't clip
	/// them. Used references stay first. Reports the changed dense range for the GPU upload.
	/// </summary>
	/// <remarks>
	/// Removals are batched: the store queues removed indices and runs this once, right before the references are read
	/// for the GPU or an index is reused.
	/// </remarks>
	[BurstCompile]
	internal struct RemoveClipBoxReferencesJob : IJob
	{
		public NativeArray<PackedClipBoxReferences> References;

		/// <summary>
		/// One bit per box index: set for removed boxes.
		/// </summary>
		[ReadOnly] public NativeBitArray Removed;

		/// <summary>
		/// Lowest and highest changed dense index (x &gt; y when nothing changed).
		/// </summary>
		[WriteOnly] public NativeReference<int2> ChangedRange;

		public void Execute()
		{
			var range = new int2(int.MaxValue, int.MinValue);

			for (var i = 0; i < References.Length; i++)
			{
				var references = References[i];
				var kept = PackedClipBoxReferences.Empty;
				var keptCount = 0;

				for (var slot = 0; slot < PackedClipBoxReferences.Count; slot++)
				{
					if (PackedClipBoxReferences.TryDecode(references[slot], out var box, out _) == false)
						break;

					if (box < Removed.Length && Removed.IsSet(box))
						continue;

					kept[keptCount++] = references[slot];
				}

				if (kept.Equals(references))
					continue;

				References[i] = kept;
				range = new int2(math.min(range.x, i), math.max(range.y, i));
			}

			ChangedRange.Value = range;
		}
	}
}
