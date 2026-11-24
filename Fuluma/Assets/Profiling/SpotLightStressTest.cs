using System.Diagnostics;
using System.Text;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Profiling;
using Saesentsessis.Fuluma.Rendering.URP;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Random = Unity.Mathematics.Random;

namespace Saesentsessis.Fuluma.Tests
{
	/// <summary>
	/// Spawns many fake spot lights inside a box, moves and turns every one of them at random each frame in a job, and
	/// shows how long the light pipeline takes: data preparation, culling and drawing.
	/// </summary>
	/// <remarks>
	/// <b>Dynamic</b> lights follow hidden root GameObjects; the job moves their <see cref="Transform"/>s and the store
	/// reads them back every frame. <b>Static</b> lights live in the store only; the job moves copies of their
	/// placements and <c>LateUpdate</c> pushes them with <see cref="LightStore.SetSpotLightPlacements"/> (one batched call),
	/// or with <see cref="LightStore.SetSpotLightPlacement"/> per light when <c>batchStaticUpdates</c> is off.
	/// <para>
	/// Pipeline timings come from <see cref="FulumaProfiling"/>'s markers, so they only exist in the Editor and in
	/// development players. They add up every camera that draws the lights (in the Editor, the Scene view too).
	/// </para>
	/// </remarks>
	[DefaultExecutionOrder(int.MinValue)]
	public class SpotLightStressTest : MonoBehaviour
	{
		private const int StaticJobBatchSize = 256;

		[Header("Lights")]
		[SerializeField, Min(1)] private int lightCount = 4096;

		[Tooltip("World-space box the lights are spawned in and kept inside.")]
		[SerializeField] private Bounds bounds = new(Vector3.zero, new Vector3(100f, 20f, 100f));

		[Tooltip("On: lights follow Transforms (dynamic pool). Off: lights are moved from code (static pool). Applied on Start.")]
		[SerializeField] private bool dynamicLights = true;

		[Tooltip("Static lights only: push the moved placements with one SetSpotLightPlacements call (on) or one SetSpotLightPlacement call per light (off).")]
		[SerializeField] private bool batchStaticUpdates = true;

		[Tooltip("Dynamic lights only: lights per hidden root GameObject. A transform job that writes runs each root on one thread, and Unity's per-root scheduling cost grows with the root count.")]
		[SerializeField, Min(1)] private int lightsPerRoot = 256;

		[Tooltip("Store to add the lights to. Empty: the store the active renderer's FulumaRenderFeature draws.")]
		[SerializeField] private LightStore store;

		[SerializeField] private Vector2 radiusRange = new(2f, 8f);
		[SerializeField] private Vector2 outerAngleRange = new(20f, 90f);
		[SerializeField] private uint seed = 1;

		[Header("Occluders")]
		[Tooltip("Opaque boxes spawned at random in the box (seeded): scene depth for the lights to hide behind, and geometry for a depth prepass to draw again. 0: none.")]
		[SerializeField, Min(0)] private int occluderCount;

		[Tooltip("Edge length range of the occluder boxes.")]
		[SerializeField] private Vector2 occluderSizeRange = new(2f, 10f);

		[Tooltip("Material of the occluder boxes. Without one they draw with the error shader (which has no depth-only pass); in players the material also brings its shader into the build.")]
		[SerializeField] private Material occluderMaterial;

		[Header("Motion")]
		[Tooltip("Move and turn the lights every frame. Off: they stay where they are (no job, no store update) until it's turned back on.")]
		[SerializeField] private bool translateLights = true;

		[Tooltip("Distance each light moves per second, in a random direction every frame.")]
		[SerializeField, Min(0f)] private float moveSpeed = 2f;

		[Tooltip("Degrees each light turns per second, around a random axis every frame.")]
		[SerializeField, Min(0f)] private float turnSpeed = 90f;

		[Header("Stats")]
		[Tooltip("Seconds between refreshes of the on-screen timings (averages over that time).")]
		[SerializeField, Min(0.05f)] private float updateInterval = 0.5f;

		private LightStore _store;

		/// <summary>
		/// The renderer feature drawing the lights, for its settings in the stats (may be null).
		/// </summary>
		private FulumaRenderFeature _feature;
		private bool _dynamic;
		private NativeArray<LightHandle> _handles;
		private JobHandle _moveJob;

		/// <summary>
		/// Whether this frame's <c>Update</c> scheduled a move, so <c>LateUpdate</c> completes and pushes it even if
		/// <see cref="translateLights"/> changed in between.
		/// </summary>
		private bool _movingThisFrame;

		// Dynamic lights
		private GameObject[] _lightObjects;
		private GameObject _occluders;
		private TransformAccessArray _transforms;

		// Static lights
		private NativeArray<Placement> _placements;

		private readonly Stopwatch _stopwatch = new();
		private readonly LightStats _stats = new();

		/// <summary>
		/// Player command-line switch that turns <see cref="translateLights"/> off: the lights stay at their seeded spawn
		/// placement, so GPU traces taken at different resolutions or after different frame counts see the same scene.
		/// </summary>
		public const string FreezeLightsArgument = "-fulumaFreezeLights";

		/// <summary>
		/// Player command-line option, followed by a number: sets the render feature's light buffer scale at start (for
		/// profiling configurations without editing the renderer asset; the change is not saved in a player).
		/// </summary>
		public const string LightBufferScaleArgument = "-fulumaLightBufferScale";

		/// <summary>
		/// Player command-line option, followed by <c>All</c> or <c>LargeOnly</c>: sets the render feature's light
		/// buffer lights at start, like <see cref="LightBufferScaleArgument"/>.
		/// </summary>
		public const string LightBufferLightsArgument = "-fulumaLightBufferLights";

		/// <summary>
		/// Player command-line option, followed by a number: sets the render feature's large-light screen size at start,
		/// like <see cref="LightBufferScaleArgument"/>.
		/// </summary>
		public const string LargeLightScreenSizeArgument = "-fulumaLargeLightScreenSize";

		/// <summary>
		/// Player command-line option, followed by <c>DepthAware</c> or <c>Bilinear</c>: sets the render feature's light
		/// buffer upsample at start, like <see cref="LightBufferScaleArgument"/>.
		/// </summary>
		public const string LightBufferUpsampleArgument = "-fulumaLightBufferUpsample";

		/// <summary>
		/// Player command-line option, followed by a whole number: overrides <see cref="lightCount"/> (on Android pass
		/// it through the launch intent: <c>adb shell am start ... -e unity "-fulumaLightCount 2048"</c>).
		/// </summary>
		public const string LightCountArgument = "-fulumaLightCount";

		/// <summary>
		/// Player command-line switch: also writes the on-screen stats to the log every update interval (logcat on
		/// Android, the player log elsewhere), for measuring without reading the screen.
		/// </summary>
		public const string LogStatsArgument = "-fulumaLogStats";

		/// <summary>
		/// Player command-line option, followed by a whole number: overrides <see cref="occluderCount"/>.
		/// </summary>
		public const string OccludersArgument = "-fulumaOccluders";

		/// <summary>
		/// Player command-line option, followed by a cell size: tiles the box with "rooms" (keep clip boxes of that size
		/// in x and z, the box's full height) and lets every light keep the room it is in. Measures clip box cost; turns
		/// the render feature's clip boxes on, whatever the renderer asset says.
		/// </summary>
		public const string ClipRoomsArgument = "-fulumaClipRooms";

		/// <summary>
		/// Player command-line switch, with <see cref="ClipRoomsArgument"/>: every room also gets a subtract box (a
		/// quarter of its size, at its center), so lights use two boxes (the keep-minus-subtract variant).
		/// </summary>
		public const string ClipCutOutsArgument = "-fulumaClipCutOuts";

		/// <summary>
		/// Player command-line switch, with <see cref="ClipRoomsArgument"/>: every room also gets a keep box "annex" (half
		/// its size, sticking half a cell out of its +x side), so lights keep the union of two boxes (an L-shaped region).
		/// Together with <see cref="ClipCutOutsArgument"/>, rooms alternate in a checkerboard: cut-out, annex.
		/// </summary>
		public const string ClipExtensionsArgument = "-fulumaClipExtensions";

		/// <summary>
		/// Player command-line switch, with <see cref="ClipRoomsArgument"/> (instead of the other room switches): every room
		/// gets two pillar cut-outs (subtract boxes a quarter of its size, a quarter of a cell left and right of its center)
		/// and its lights subtract both, without keeping the room: two subtracts per light. Together with
		/// <see cref="ClipCutOutsArgument"/>, lights keep the room too: three boxes per light (the general variant).
		/// </summary>
		public const string ClipTwoCutOutsArgument = "-fulumaClipTwoCutOuts";

		/// <summary>
		/// Player command-line switch, with <see cref="ClipRoomsArgument"/>: every clip box follows a GameObject's
		/// transform (dynamic boxes, re-packed and uploaded every frame) instead of a fixed placement.
		/// </summary>
		public const string ClipDynamicBoxesArgument = "-fulumaClipDynamicBoxes";

		/// <summary>
		/// Player command-line option, followed by <c>Auto</c>, <c>AfterOpaques</c> or <c>BeforeOpaques</c>: sets the
		/// render feature's light buffer timing at start, like <see cref="LightBufferScaleArgument"/>.
		/// </summary>
		public const string LightBufferTimingArgument = "-fulumaLightBufferTiming";

		/// <summary>
		/// Player command-line switch: turns the render feature's <c>UseSceneDepth</c> off at start.
		/// </summary>
		public const string NoSceneDepthArgument = "-fulumaNoSceneDepth";

		/// <summary>
		/// Player command-line switch: turns the render feature's <c>UseWideConePrecision</c> on at start.
		/// </summary>
		public const string WideConePrecisionArgument = "-fulumaWideConePrecision";

		/// <summary>
		/// Player command-line switch: the lights follow GameObjects (the dynamic pool) whatever the scene says.
		/// </summary>
		public const string DynamicLightsArgument = "-fulumaDynamicLights";

		/// <summary>
		/// Player command-line switch: turns the render feature's <c>UseNearPlaneFade</c> off at start.
		/// </summary>
		public const string NoNearFadeArgument = "-fulumaNoNearFade";

		/// <summary>
		/// Player command-line switch: turns the render feature's <c>UseSpotLightLods</c> off at start.
		/// </summary>
		public const string NoLodsArgument = "-fulumaNoLods";

		/// <summary>
		/// Player command-line switch followed by a <see cref="ScatteringPhase"/> name: the feature's phase at start.
		/// </summary>
		public const string PhaseArgument = "-fulumaPhase";

		/// <summary>
		/// Player command-line switch followed by a number: the feature's scattering anisotropy at start.
		/// </summary>
		public const string AnisotropyArgument = "-fulumaAnisotropy";

		/// <summary>
		/// Player command-line switch followed by a <see cref="DistanceFalloff"/> name: the feature's falloff at start.
		/// </summary>
		public const string FalloffArgument = "-fulumaFalloff";

		/// <summary>
		/// Player command-line switch followed by a <see cref="LampGlare"/> name: the feature's lamp glare at start.
		/// </summary>
		public const string GlareArgument = "-fulumaGlare";

		/// <summary>
		/// Player command-line switch followed by a <c>RenderPassEvent</c> name: the feature's pass event at start.
		/// </summary>
		public const string EventArgument = "-fulumaEvent";

		/// <summary>
		/// Player command-line switch followed by 1, 2, 4 or 8: the active URP asset's MSAA sample count at start.
		/// </summary>
		public const string MsaaArgument = "-fulumaMsaa";

		private void Start()
		{
			_feature = FindFeature();
			ApplyCommandLine(System.Environment.GetCommandLineArgs());

			_store = store != null ? store : _feature != null ? _feature.Store : LightStore.Default;
			_dynamic = dynamicLights || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), DynamicLightsArgument) >= 0;
			_handles = new NativeArray<LightHandle>(lightCount, Allocator.Persistent);

			if (_dynamic)
				SpawnDynamicLights();
			else
				SpawnStaticLights();

			SpawnOccluders();
			AddClipRooms(System.Environment.GetCommandLineArgs());
		}

		private void ApplyCommandLine(string[] arguments)
		{
			if (System.Array.IndexOf(arguments, FreezeLightsArgument) >= 0)
				translateLights = false;

			if (System.Array.IndexOf(arguments, LogStatsArgument) >= 0)
				_stats.LogToConsole = true;

			if (TryGetArgumentValue(arguments, OccludersArgument, out var occludersText)
			    && int.TryParse(occludersText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var occluders)
			    && occluders >= 0)
				occluderCount = occluders;

			if (_feature != null && System.Array.IndexOf(arguments, NoSceneDepthArgument) >= 0)
				_feature.UseSceneDepth = false;

			if (_feature != null && System.Array.IndexOf(arguments, WideConePrecisionArgument) >= 0)
				_feature.UseWideConePrecision = true;

			if (_feature != null && TryGetArgumentValue(arguments, LightBufferTimingArgument, out var timingText)
			    && System.Enum.TryParse(timingText, true, out LightBufferTiming timing))
				_feature.LightBufferTiming = timing;

			if (TryGetArgumentValue(arguments, MsaaArgument, out var msaaText)
			    && int.TryParse(msaaText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var msaa)
			    && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
				pipeline.msaaSampleCount = msaa;

			if (TryGetArgumentValue(arguments, LightCountArgument, out var countText)
			    && int.TryParse(countText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
			    && count > 0)
				lightCount = count;

			if (_feature == null)
				return;

			if (TryGetArgumentValue(arguments, LightBufferScaleArgument, out var scaleText)
			    && float.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale))
				_feature.LightBufferScale = scale;

			if (TryGetArgumentValue(arguments, LightBufferLightsArgument, out var lightsText)
			    && System.Enum.TryParse(lightsText, true, out LightBufferLights lights))
				_feature.LightBufferLights = lights;

			if (TryGetArgumentValue(arguments, LightBufferUpsampleArgument, out var upsampleText)
			    && System.Enum.TryParse(upsampleText, true, out LightBufferUpsample upsample))
				_feature.LightBufferUpsample = upsample;

			if (TryGetArgumentValue(arguments, LargeLightScreenSizeArgument, out var sizeText)
			    && float.TryParse(sizeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size))
				_feature.LargeLightScreenSize = size;

			ApplyLightModelArguments(arguments);
		}

		private void ApplyLightModelArguments(string[] arguments)
		{
			if (System.Array.IndexOf(arguments, NoNearFadeArgument) >= 0)
				_feature.UseNearPlaneFade = false;

			if (System.Array.IndexOf(arguments, NoLodsArgument) >= 0)
				_feature.UseSpotLightLods = false;

			if (TryGetArgumentValue(arguments, PhaseArgument, out var phaseText)
			    && System.Enum.TryParse(phaseText, true, out ScatteringPhase phase))
				_feature.ScatteringPhase = phase;

			if (TryGetArgumentValue(arguments, AnisotropyArgument, out var anisotropyText)
			    && float.TryParse(anisotropyText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var anisotropy))
				_feature.ScatteringAnisotropy = anisotropy;

			if (TryGetArgumentValue(arguments, FalloffArgument, out var falloffText)
			    && System.Enum.TryParse(falloffText, true, out DistanceFalloff falloff))
				_feature.DistanceFalloff = falloff;

			if (TryGetArgumentValue(arguments, GlareArgument, out var glareText)
			    && System.Enum.TryParse(glareText, true, out LampGlare glare))
				_feature.LampGlare = glare;

			if (TryGetArgumentValue(arguments, EventArgument, out var eventText)
			    && System.Enum.TryParse(eventText, true, out RenderPassEvent renderPassEvent))
				_feature.RenderPassEvent = renderPassEvent;
		}

		private static bool TryGetArgumentValue(string[] arguments, string name, out string value)
		{
			var index = System.Array.IndexOf(arguments, name);
			value = index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
			return value != null;
		}

		private void OnEnable() => _stats.Start();

		private void OnDisable()
		{
			_moveJob.Complete();
			_stats.Stop();
		}

		private void OnDestroy()
		{
			_moveJob.Complete();
			RemoveLights();
		}

		private void Update()
		{
			_movingThisFrame = _handles.IsCreated && translateLights;
			if (_movingThisFrame == false)
				return;

			_stopwatch.Restart();
			_moveJob = _dynamic ? ScheduleDynamicMove() : ScheduleStaticMove();
			JobHandle.ScheduleBatchedJobs();
			_stats.MoveSchedule.Add(ElapsedMilliseconds());
		}

		private void LateUpdate()
		{
			if (_handles.IsCreated == false)
				return;

			// Paused: nothing moved, so there's nothing to wait for or push; the move timings count as 0 this frame.
			if (_movingThisFrame)
			{
				_stopwatch.Restart();
				_moveJob.Complete();
				_stats.MoveWait.Add(ElapsedMilliseconds());
			}

			if (_dynamic == false && _movingThisFrame)
			{
				_stopwatch.Restart();
				PushStaticTransforms();
				_stats.StoreUpdate.Add(ElapsedMilliseconds());
				_stats.BatchedStoreUpdate = batchStaticUpdates;
			}

			_stats.EndFrame(Time.unscaledDeltaTime, updateInterval, lightCount, _dynamic, _feature);
		}

		private void OnGUI() => _stats.Draw();

		private void OnDrawGizmosSelected()
		{
			Gizmos.color = Color.yellow;
			Gizmos.DrawWireCube(bounds.center, bounds.size);
		}

		// ---------- Spawning ----------

		private void SpawnDynamicLights()
		{
			var perRoot = math.max(1, lightsPerRoot);
			_lightObjects = new GameObject[(lightCount + perRoot - 1) / perRoot];
			var transforms = new Transform[lightCount];
			var random = new Random(math.max(seed, 1u));

			for (var i = 0; i < lightCount; i++)
			{
				var root = i / perRoot;
				if (_lightObjects[root] == null)
					_lightObjects[root] = new GameObject($"Fuluma Stress Lights {root}") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };

				var lightObject = new GameObject($"Fuluma Stress Light {i}") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
				lightObject.transform.SetParent(_lightObjects[root].transform, false);
				lightObject.transform.SetPositionAndRotation(RandomPosition(ref random), random.NextQuaternionRotation());

				transforms[i] = lightObject.transform;
				_handles[i] = _store.AddSpotLight(lightObject.transform, RandomLight(ref random));
			}

			_transforms = new TransformAccessArray(transforms);
		}

		/// <summary>
		/// <see cref="ClipRoomsArgument"/>: a grid of keep boxes over the box, each light referencing the one it is in
		/// (and, with <see cref="ClipCutOutsArgument"/>, a subtract box in its middle).
		/// </summary>
		private void AddClipRooms(string[] arguments)
		{
			if (TryGetArgumentValue(arguments, ClipRoomsArgument, out var sizeText) == false
			    || float.TryParse(sizeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cellSize) == false
			    || cellSize <= 0f)
				return;

			if (_feature != null)
				_feature.UseClipBoxes = true;

			var cutOuts = System.Array.IndexOf(arguments, ClipCutOutsArgument) >= 0;
			var extensions = System.Array.IndexOf(arguments, ClipExtensionsArgument) >= 0;
			var twoCutOuts = System.Array.IndexOf(arguments, ClipTwoCutOutsArgument) >= 0;
			var dynamicBoxes = System.Array.IndexOf(arguments, ClipDynamicBoxesArgument) >= 0;

			ClipBoxHandle AddRoomBox(Placement placement, ClipBoxData box)
			{
				if (dynamicBoxes == false)
					return _store.AddClipBox(placement, box);

				var follow = new GameObject("Clip Box").transform;
				follow.SetParent(transform, false);
				follow.SetPositionAndRotation(placement.Position, placement.Rotation);
				return _store.AddClipBox(follow, box);
			}

			// Which second box a room gets: a cut-out, an annex, or (both switches) alternating.
			bool HasAnnex(int x, int z) => extensions && (cutOuts == false || ((x + z) & 1) == 1);
			var min = (float3)bounds.min;
			var size = (float3)bounds.size;
			var cells = new int2(math.max(1, (int)math.ceil(size.x / cellSize)), math.max(1, (int)math.ceil(size.z / cellSize)));
			var rooms = new ClipBoxHandle[cells.x * cells.y];
			var holes = cutOuts || extensions || twoCutOuts ? new ClipBoxHandle[rooms.Length] : null;
			var secondHoles = twoCutOuts ? new ClipBoxHandle[rooms.Length] : null;

			for (var z = 0; z < cells.y; z++)
			for (var x = 0; x < cells.x; x++)
			{
				var center = new Vector3(min.x + (x + 0.5f) * cellSize, bounds.center.y, min.z + (z + 0.5f) * cellSize);
				var placement = new Placement { Position = center, Rotation = Quaternion.identity };
				rooms[z * cells.x + x] = AddRoomBox(placement, new ClipBoxData { Size = new Vector3(cellSize, size.y, cellSize) });
				if (twoCutOuts)
				{
					var pillar = new ClipBoxData { Size = new Vector3(cellSize * 0.25f, size.y, cellSize * 0.25f) };
					pillar.Center = new Vector3(-cellSize * 0.25f, 0f, 0f);
					holes[z * cells.x + x] = AddRoomBox(placement, pillar);
					pillar.Center = new Vector3(cellSize * 0.25f, 0f, 0f);
					secondHoles[z * cells.x + x] = AddRoomBox(placement, pillar);
				}
				else if (HasAnnex(x, z))
					holes[z * cells.x + x] = AddRoomBox(placement,
						new ClipBoxData { Center = new Vector3(cellSize * 0.5f, 0f, 0f), Size = new Vector3(cellSize * 0.5f, size.y, cellSize * 0.5f) });
				else if (cutOuts)
					holes[z * cells.x + x] = AddRoomBox(placement,
						new ClipBoxData { Size = new Vector3(cellSize * 0.25f, size.y, cellSize * 0.25f) });
			}

			var keepAndTwoCutOuts = twoCutOuts && cutOuts;
			var references = new ClipBoxReference[keepAndTwoCutOuts ? 3 : holes != null ? 2 : 1];
			for (var i = 0; i < lightCount; i++)
			{
				float3 position = _dynamic ? _transforms[i].position : _placements[i].Position;
				var cell = math.clamp((int2)((position.xz - min.xz) / cellSize), 0, cells - 1);
				var room = cell.y * cells.x + cell.x;
				references[0] = ClipBoxReference.Keep(rooms[room]);
				if (keepAndTwoCutOuts)
				{
					references[1] = ClipBoxReference.Subtract(holes[room]);
					references[2] = ClipBoxReference.Subtract(secondHoles[room]);
				}
				else if (twoCutOuts)
				{
					references[0] = ClipBoxReference.Subtract(holes[room]);
					references[1] = ClipBoxReference.Subtract(secondHoles[room]);
				}
				else if (holes != null)
					references[1] = HasAnnex(cell.x, cell.y) ? ClipBoxReference.Keep(holes[room]) : ClipBoxReference.Subtract(holes[room]);
				_store.SetClipBoxes(_handles[i], references);
			}

			UnityEngine.Debug.Log($"[FulumaStats] {rooms.Length} clip rooms of {cellSize} m{(dynamicBoxes ? " (dynamic boxes)" : "")}{(keepAndTwoCutOuts ? " with two cut-outs each" : twoCutOuts ? " (two cut-outs each, rooms not kept)" : cutOuts && extensions ? " with cut-outs and annexes" : cutOuts ? " with cut-outs" : extensions ? " with annexes" : "")}, {_store.ClipBoxCount} boxes.");
		}

		private void SpawnStaticLights()
		{
			_placements = new NativeArray<Placement>(lightCount, Allocator.Persistent);
			var random = new Random(math.max(seed, 1u));

			for (var i = 0; i < lightCount; i++)
			{
				_placements[i] = new Placement { Position = RandomPosition(ref random), Rotation = random.NextQuaternionRotation() };
				_handles[i] = _store.AddSpotLight(_placements[i], RandomLight(ref random));
			}
		}

		private float3 RandomPosition(ref Random random) => random.NextFloat3(bounds.min, bounds.max);

		private SpotLightData RandomLight(ref Random random)
		{
			var light = SpotLightData.Default;
			light.Color = Color.HSVToRGB(random.NextFloat(), 0.6f, 1f);
			light.Range = random.NextFloat(radiusRange.x, radiusRange.y);
			light.OuterAngleDegrees = random.NextFloat(outerAngleRange.x, outerAngleRange.y);
			light.InnerAngleDegrees = light.OuterAngleDegrees * 0.6f;
			return light;
		}

		/// <summary>
		/// <see cref="occluderCount"/> boxes with <see cref="occluderMaterial"/> (none: the error shader), under one hidden root.
		/// </summary>
		private void SpawnOccluders()
		{
			if (occluderCount <= 0)
				return;

			_occluders = new GameObject("Fuluma Stress Occluders") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
			var random = new Random(math.max(seed, 1u) * 7919u + 1u);

			// Not CreatePrimitive: it adds a BoxCollider, and without the Physics module every call logs an error.
			var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");

			for (var i = 0; i < occluderCount; i++)
			{
				var box = new GameObject("Fuluma Stress Occluder", typeof(MeshFilter), typeof(MeshRenderer))
				{
					hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
				};
				box.GetComponent<MeshFilter>().sharedMesh = cube;
				box.transform.SetParent(_occluders.transform, false);
				box.transform.SetPositionAndRotation(RandomPosition(ref random), random.NextQuaternionRotation());
				box.transform.localScale = random.NextFloat3(occluderSizeRange.x, occluderSizeRange.y);

				if (occluderMaterial != null)
					box.GetComponent<MeshRenderer>().sharedMaterial = occluderMaterial;
			}
		}

		private void RemoveLights()
		{
			if (_occluders != null)
				Destroy(_occluders);

			_occluders = null;

			if (_handles.IsCreated && _store != null)
				foreach (var handle in _handles)
					_store.Remove(handle); // false for lights a play-mode switch already released

			if (_lightObjects != null)
				foreach (var lightObject in _lightObjects)
					if (lightObject != null)
						Destroy(lightObject);

			_lightObjects = null;

			if (_transforms.isCreated)
				_transforms.Dispose();

			DisposeIfCreated(ref _handles);
			DisposeIfCreated(ref _placements);
		}

		private static void DisposeIfCreated<T>(ref NativeArray<T> array) where T : struct
		{
			if (array.IsCreated)
				array.Dispose();

			array = default;
		}

		// ---------- Per frame ----------

		private RandomWalk Walk() => new()
		{
			Min = bounds.min,
			Max = bounds.max,
			MoveStep = moveSpeed * Time.deltaTime,
			TurnStep = math.radians(turnSpeed) * Time.deltaTime,
			Seed = seed + (uint)Time.frameCount
		};

		private JobHandle ScheduleDynamicMove()
			=> new MoveTransformsJob { Walk = Walk() }.Schedule(_transforms);

		private JobHandle ScheduleStaticMove()
			=> new MovePlacementsJob { Walk = Walk(), Placements = _placements }.Schedule(lightCount, StaticJobBatchSize);

		private void PushStaticTransforms()
		{
			if (batchStaticUpdates)
			{
				_store.SetSpotLightPlacements(_handles, _placements);
				return;
			}

			for (var i = 0; i < lightCount; i++)
				_store.SetSpotLightPlacement(_handles[i], _placements[i]);
		}

		private double ElapsedMilliseconds() => _stopwatch.Elapsed.TotalMilliseconds;

		/// <summary>
		/// The first active <see cref="FulumaRenderFeature"/> in the current URP asset, or null.
		/// </summary>
		private static FulumaRenderFeature FindFeature()
		{
			if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset)
				foreach (var rendererData in asset.rendererDataList)
				{
					if (rendererData == null)
						continue;

					foreach (var feature in rendererData.rendererFeatures)
						if (feature is FulumaRenderFeature { isActive: true } fuluma)
							return fuluma;
				}

			return null;
		}

		// ---------- Jobs ----------

		/// <summary>
		/// One random step per light and frame: a move of fixed length in a random direction, kept inside the box, and
		/// a turn of up to the turn step around a random axis.
		/// </summary>
		private struct RandomWalk
		{
			public float3 Min, Max;
			public float MoveStep, TurnStep;
			public uint Seed;

			public void Step(int index, ref float3 position, ref quaternion rotation)
			{
				var random = new Random(math.hash(new uint2((uint)index, Seed)) | 1u);

				position = math.clamp(position + random.NextFloat3Direction() * MoveStep, Min, Max);

				var turn = quaternion.AxisAngle(random.NextFloat3Direction(), random.NextFloat(-TurnStep, TurnStep));
				rotation = math.normalize(math.mul(rotation, turn));
			}
		}

		/// <summary>
		/// Dynamic path: moves the lights' Transforms; the store reads them when the frame renders.
		/// </summary>
		[BurstCompile]
		private struct MoveTransformsJob : IJobParallelForTransform
		{
			public RandomWalk Walk;

			public void Execute(int index, TransformAccess transform)
			{
				transform.GetPositionAndRotation(out var position, out var rotation);

				float3 p = position;
				quaternion r = rotation;
				Walk.Step(index, ref p, ref r);

				transform.SetPositionAndRotation(p, r);
			}
		}

		/// <summary>
		/// Static path: moves copies of the placements; <c>LateUpdate</c> pushes them into the store.
		/// </summary>
		[BurstCompile]
		private struct MovePlacementsJob : IJobParallelFor
		{
			public RandomWalk Walk;
			public NativeArray<Placement> Placements;

			public void Execute(int index)
			{
				var placement = Placements[index];
				float3 position = placement.Position;
				quaternion rotation = placement.Rotation;
				Walk.Step(index, ref position, ref rotation);

				placement.Position = position;
				placement.Rotation = rotation;
				Placements[index] = placement;
			}
		}

		// ---------- Stats ----------

		/// <summary>
		/// Sum of a per-frame timing over the current interval, and its average over the last one.
		/// </summary>
		private sealed class Timing
		{
			private readonly string _label;
			private double _sum;

			public Timing(string label) => _label = label;

			public double Average { get; private set; }

			public void Add(double milliseconds) => _sum += milliseconds;

			public void Close(int frames)
			{
				Average = frames > 0 ? _sum / frames : 0.0;
				_sum = 0.0;
			}

			public void Append(StringBuilder text) => text.Append("  ").Append(_label.PadRight(30)).AppendFormat("{0,8:F3} ms\n", Average);
		}

		/// <summary>
		/// A <see cref="FulumaProfiling"/> marker read through a <see cref="ProfilerRecorder"/>: its total of the last
		/// finished frame, over every thread.
		/// </summary>
		private sealed class MarkerTiming
		{
			private readonly string _marker;
			private ProfilerRecorder _recorder;

			public MarkerTiming(string label, string marker)
			{
				Timing = new Timing(label);
				_marker = marker;
			}

			public Timing Timing { get; }

			public bool IsRecording => _recorder.Valid;

			public void Start()
				=> _recorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, _marker, 1,
					ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached);

			public void Stop() => _recorder.Dispose();

			public void Sample()
			{
				if (_recorder.Valid)
					Timing.Add(_recorder.LastValue * 1e-6);
			}
		}

		private sealed class LightStats
		{
			public readonly Timing MoveSchedule = new("Move job schedule (Update)");
			public readonly Timing MoveWait = new("Move job wait (LateUpdate)");
			public readonly Timing StoreUpdate = new("Push to store");

			/// <summary>
			/// Whether <see cref="StoreUpdate"/> timed the batched call (label only).
			/// </summary>
			public bool BatchedStoreUpdate;

			/// <summary>
			/// Also log the stats text every interval (see <see cref="LogStatsArgument"/>).
			/// </summary>
			public bool LogToConsole;

			private readonly MarkerTiming _mirrorUpload = new("GPU mirror upload", FulumaProfiling.MirrorUploadName);
			private readonly MarkerTiming _mirrorComplete = new("GPU mirror copy wait", FulumaProfiling.MirrorCompleteName);
			private readonly MarkerTiming _cullingSchedule = new("Schedule (main)", FulumaProfiling.CullingScheduleName);
			private readonly MarkerTiming _cullingJob = new("Job (all workers)", FulumaProfiling.CullingJobName);
			private readonly MarkerTiming _cullingWait = new("Wait (main)", FulumaProfiling.CullingWaitName);
			private readonly MarkerTiming _cullingUpload = new("Index upload (main)", FulumaProfiling.CullingUploadName);
			private readonly MarkerTiming _materialBinding = new("Material binding (main)", FulumaProfiling.MaterialBindingName);

			private readonly Timing _drawCpu = new("Draw pass CPU");
			private readonly Timing _drawGpu = new("Draw pass GPU");
			private readonly Timing _downsampleGpu = new("Depth downsample GPU");
			private readonly Timing _compositeGpu = new("Composite GPU");
			private readonly Timing _drawLowResolutionGpu = new("Draw large lights GPU (low res)");
			private readonly Timing _frame = new("Frame");

			private readonly MarkerTiming[] _markers;
			private readonly Timing[] _timings;

			private readonly StringBuilder _builder = new();
			private readonly GUIContent _content = new("Collecting...");
			private GUIStyle _style;

			/// <summary>
			/// Monospace fonts by OS family name, in order of preference: Windows, macOS, Windows, Android, Android.
			/// </summary>
			private static readonly string[] MonospaceFonts = { "Consolas", "Menlo", "Courier New", "Droid Sans Mono", "Cutive Mono" };

			/// <summary>
			/// GUI size factor for dense screens (phones): 1 up to 160 dpi.
			/// </summary>
			public static float GuiScale => Mathf.Max(1f, Screen.dpi / 160f);

			/// <summary>
			/// The first installed font of <see cref="MonospaceFonts"/> (names compared without spaces and case), else
			/// null: the skin's font. Asking for a font the OS doesn't have gives a font without glyphs, and the box then
			/// showed other text or nothing (Android).
			/// </summary>
			private static Font FindMonospaceFont(int size)
			{
				static string Key(string name) => name.Replace(" ", "").ToLowerInvariant();

				var installed = Font.GetOSInstalledFontNames();
				foreach (var preferred in MonospaceFonts)
					foreach (var name in installed)
						if (Key(name) == Key(preferred))
							return Font.CreateDynamicFontFromOSFont(name, size);

				return null;
			}
			private float _elapsed;
			private int _frames;

			public LightStats()
			{
				_markers = new[] { _mirrorUpload, _mirrorComplete, _cullingSchedule, _cullingJob, _cullingWait, _cullingUpload, _materialBinding };
				_timings = new[]
				{
					MoveSchedule, MoveWait, StoreUpdate,
					_mirrorUpload.Timing, _mirrorComplete.Timing,
					_cullingSchedule.Timing, _cullingJob.Timing, _cullingWait.Timing, _cullingUpload.Timing,
					_materialBinding.Timing, _drawCpu, _drawGpu, _drawLowResolutionGpu, _downsampleGpu, _compositeGpu, _frame
				};
			}

			public void Start()
			{
				foreach (var marker in _markers)
					marker.Start();

				FulumaProfiling.DrawSampler.enableRecording = true;
				FulumaProfiling.DrawLowResolutionSampler.enableRecording = true;
				FulumaProfiling.DownsampleDepthSampler.enableRecording = true;
				FulumaProfiling.CompositeSampler.enableRecording = true;
			}

			public void Stop()
			{
				foreach (var marker in _markers)
					marker.Stop();

				FulumaProfiling.DrawSampler.enableRecording = false;
				FulumaProfiling.DrawLowResolutionSampler.enableRecording = false;
				FulumaProfiling.DownsampleDepthSampler.enableRecording = false;
				FulumaProfiling.CompositeSampler.enableRecording = false;
			}

			public void EndFrame(float deltaTime, float interval, int lightCount, bool dynamic, FulumaRenderFeature feature)
			{
				foreach (var marker in _markers)
					marker.Sample();

				_drawCpu.Add(FulumaProfiling.DrawSampler.cpuElapsedTime);
				_drawGpu.Add(FulumaProfiling.DrawSampler.gpuElapsedTime);
				_drawLowResolutionGpu.Add(FulumaProfiling.DrawLowResolutionSampler.gpuElapsedTime);
				_downsampleGpu.Add(FulumaProfiling.DownsampleDepthSampler.gpuElapsedTime);
				_compositeGpu.Add(FulumaProfiling.CompositeSampler.gpuElapsedTime);
				_frame.Add(deltaTime * 1000.0);

				_frames++;
				_elapsed += deltaTime;

				if (_elapsed < interval)
					return;

				foreach (var timing in _timings)
					timing.Close(_frames);

				_content.text = Format(lightCount, dynamic, feature);
				if (LogToConsole)
					UnityEngine.Debug.Log("[FulumaStats]\n" + _content.text);
				_elapsed = 0f;
				_frames = 0;
			}

			public void Draw()
			{
				if (_style == null)
				{
					var scale = GuiScale;
					var fontSize = Mathf.RoundToInt(13 * scale);
					_style = new GUIStyle(GUI.skin.box)
					{
						alignment = TextAnchor.UpperLeft,
						font = FindMonospaceFont(fontSize),
						fontSize = fontSize,
						padding = new RectOffset(Mathf.RoundToInt(10 * scale), Mathf.RoundToInt(10 * scale),
							Mathf.RoundToInt(8 * scale), Mathf.RoundToInt(8 * scale))
					};
				}

				var size = _style.CalcSize(_content);

				// Shrink the text to fit short screens (a 720-pixel-high phone in landscape).
				var maxHeight = Screen.height - 20f;
				if (size.y > maxHeight && _style.fontSize > 8)
				{
					_style.fontSize = Mathf.Max(8, Mathf.FloorToInt(_style.fontSize * maxHeight / size.y));
					size = _style.CalcSize(_content);
				}

				GUI.Box(new Rect(10f, 10f, size.x, size.y), _content, _style);
			}

			/// <summary>
			/// The light buffer's size in pixels for the main camera: its target size (with URP's render scale) times
			/// <paramref name="scale"/>, rounded up like the buffer itself.
			/// </summary>
			private static string LightBufferSize(float scale)
			{
				var camera = Camera.main;
				if (camera == null)
					return "no main camera";

				var renderScale = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline ? pipeline.renderScale : 1f;
				var width = Mathf.Max(1, (int)(camera.pixelWidth * renderScale));
				var height = Mathf.Max(1, (int)(camera.pixelHeight * renderScale));
				// Same rounding as DownsampledLightBuffer.ScaledSize (internal to the package).
				return $"{Mathf.Max(1, Mathf.CeilToInt(width * scale - 1e-3f))}x{Mathf.Max(1, Mathf.CeilToInt(height * scale - 1e-3f))}";
			}

			private string Format(int lightCount, bool dynamic, FulumaRenderFeature feature)
			{
				var text = _builder.Clear();
				var frameMs = _frame.Average;

				text.AppendFormat("Fuluma spot lights: {0} {1}, {2} drawn by the last camera\n", lightCount,
					dynamic ? "dynamic" : "static", FulumaProfiling.LastVisibleSpotLights);
				text.AppendFormat("Proxy triangles {0:N0}, lights per LOD ", FulumaProfiling.LastSpotLightTriangles);
				var perLod = FulumaProfiling.LastSpotLightsPerLod;
				var lastLod = perLod.Length - 1;
				while (lastLod > 0 && perLod[lastLod] == 0)
					lastLod--;
				for (var lod = 0; lod <= lastLod; lod++)
					text.Append(lod == 0 ? "" : " / ").Append(perLod[lod]);
				text.Append('\n');
				if (feature != null)
					text.AppendFormat("LODs {0}, clip boxes {2}, wide cones {3}, light buffer {1}\n", feature.UseSpotLightLods ? "on" : "off",
						feature.LightBufferScale < LightBufferScaling.Full
							? $"{LightBufferSize(feature.LightBufferScale)} ({feature.LightBufferScale:0.###} scale)" + (feature.LightBufferLights == LightBufferLights.LargeOnly
								? $", large lights only (>= {feature.LargeLightScreenSize:0.###} of the screen height)"
								: "")
							: $"full resolution ({LightBufferSize(1f)})",
						feature.UseClipBoxes ? (feature.Store != null && feature.Store.ClipBoxCount > 0 ? "on" : "on, none in the store") : "off",
						feature.UseWideConePrecision ? "on" : "off");
				text.AppendFormat("Frame {0:F2} ms ({1:F0} fps), averages per frame\n\n", frameMs, frameMs > 0 ? 1000.0 / frameMs : 0);

				text.Append("Data preparation\n");
				MoveSchedule.Append(text);
				MoveWait.Append(text);
				if (dynamic == false)
				{
					text.Append(BatchedStoreUpdate ? "  SetSpotLightPlacements, batched:\n" : "  SetSpotLightPlacement x N:\n");
					StoreUpdate.Append(text);
				}
				_mirrorUpload.Timing.Append(text);
				_mirrorComplete.Timing.Append(text);

				text.Append("Culling\n");
				_cullingSchedule.Timing.Append(text);
				_cullingJob.Timing.Append(text);
				_cullingWait.Timing.Append(text);
				_cullingUpload.Timing.Append(text);

				text.Append("Rendering\n");
				_materialBinding.Timing.Append(text);
				_drawCpu.Append(text);
				if (SystemInfo.supportsGpuRecorder)
				{
					_drawGpu.Append(text);
					if (feature != null && feature.LightBufferScale < LightBufferScaling.Full)
					{
						if (feature.LightBufferLights == LightBufferLights.LargeOnly)
							_drawLowResolutionGpu.Append(text);

						_downsampleGpu.Append(text);
						_compositeGpu.Append(text);
					}
				}
				else
					text.Append("  Draw pass GPU: not supported on this device\n");

				if (_mirrorUpload.IsRecording == false)
					text.Append("\nPipeline markers need the Editor or a development build.");
				else
					text.Append("\nMarker timings add up every camera that draws the lights.");

				return text.ToString();
			}
		}
	}
}
