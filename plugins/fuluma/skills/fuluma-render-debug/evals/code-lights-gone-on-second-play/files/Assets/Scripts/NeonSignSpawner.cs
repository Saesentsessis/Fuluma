using System.Collections.Generic;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

// Adds one fake spot light per neon sign anchor through the light store API (we have ~400 of them, too many for components).
public sealed class NeonSignSpawner : MonoBehaviour
{
	private static bool s_spawned;
	private static readonly List<LightHandle> s_handles = new();

	[SerializeField] private LightStore store;
	[SerializeField] private Transform[] anchors;
	[SerializeField] private Color32 color = new(255, 60, 200, 255);

	private void Start()
	{
		// Only spawn once: the spawner lives in two additively loaded scenes.
		if (s_spawned)
			return;

		var data = new SpotLightData { Color = color, Intensity = 3f, Range = 2.5f, InnerAngleDegrees = 60f, OuterAngleDegrees = 90f };
		foreach (var anchor in anchors)
			s_handles.Add(store.AddSpotLight(new Placement { Position = anchor.position, Rotation = anchor.rotation }, data));

		s_spawned = true;
	}
}
