using Unity.Mathematics;

namespace Saesentsessis.Fuluma.Rendering.Culling
{
	/// <summary>
	/// How many lights of one visible-light region fall into each clip class (<see cref="SpotLightClipClassJob"/>), in the
	/// order the region lists them. The last class, <see cref="SpotLightClipClassJob.General"/>, isn't stored: it is the
	/// rest of the region.
	/// </summary>
	internal struct SpotLightClipClassCounts
	{
		/// <summary>
		/// Classes stored here: every class but the general one.
		/// </summary>
		public const int StoredCount = SpotLightClipClassJob.ClassCount - 1;

		public int NoBoxes;
		public int OneBox;
		public int KeepMinusSubtract;
		public int TwoKeeps;
		public int TwoSubtracts;

		public int this[int clipClass]
		{
			readonly get => clipClass switch
			{
				SpotLightClipClassJob.NoBoxes => NoBoxes,
				SpotLightClipClassJob.OneBox => OneBox,
				SpotLightClipClassJob.KeepMinusSubtract => KeepMinusSubtract,
				SpotLightClipClassJob.TwoKeeps => TwoKeeps,
				SpotLightClipClassJob.TwoSubtracts => TwoSubtracts,
				_ => 0
			};
			set
			{
				switch (clipClass)
				{
					case SpotLightClipClassJob.NoBoxes: NoBoxes = value; break;
					case SpotLightClipClassJob.OneBox: OneBox = value; break;
					case SpotLightClipClassJob.KeepMinusSubtract: KeepMinusSubtract = value; break;
					case SpotLightClipClassJob.TwoKeeps: TwoKeeps = value; break;
					case SpotLightClipClassJob.TwoSubtracts: TwoSubtracts = value; break;
				}
			}
		}

		/// <summary>
		/// Lights in the stored classes (the general class starts after them).
		/// </summary>
		public readonly int Stored => NoBoxes + OneBox + KeepMinusSubtract + TwoKeeps + TwoSubtracts;

		/// <summary>
		/// The first <paramref name="kept"/> lights of a region with these counts (what a cut at the visible-light maximum
		/// keeps): classes are trimmed in order.
		/// </summary>
		public readonly SpotLightClipClassCounts Trimmed(int kept)
		{
			var result = default(SpotLightClipClassCounts);
			for (var clipClass = 0; clipClass < StoredCount; clipClass++)
			{
				var count = math.min(this[clipClass], kept);
				result[clipClass] = count;
				kept -= count;
			}

			return result;
		}

		public override readonly string ToString()
			=> $"({NoBoxes}, {OneBox}, {KeepMinusSubtract}, {TwoKeeps}, {TwoSubtracts})";
	}
}
