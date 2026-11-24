using Saesentsessis.Fuluma.Data;
using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// Small store inputs shared by the store and mirror tests.
	/// </summary>
	internal static class TestData
	{
		public static Placement StaticAt(float x)
			=> new() { Position = new Vector3(x, 0f, 0f), Rotation = Quaternion.identity };

		public static SpotLightData Spot(float radius)
			=> new()
			{
				Color = new Color32(255, 255, 255, 255),
				Range = radius,
				InnerAngleDegrees = 20f,
				OuterAngleDegrees = 30f
			};
	}
}
