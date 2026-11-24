using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.SystemMetrics;
using UnityEngine;

/// <summary>
/// Development-player helper for on-device measurement (project only, not part of the package): in every player it
/// uncaps the frame rate to the display's fastest refresh rate; with the
/// <c>-fulumaLogStats</c> switch it logs, once per second, the CPU/GPU frame times from <see cref="FrameTimingManager"/>
/// and per-frame averages of Mali hardware counters (System Metrics Mali package) under the tag <c>[FulumaGpu]</c>.
/// Android: <c>adb shell am start -n &lt;package&gt;/&lt;activity&gt; -e unity "-fulumaLogStats"</c>, read with
/// <c>adb logcat -s Unity</c>.
/// </summary>
public sealed class MobileGpuStats : MonoBehaviour
{
	private const string LogStatsArgument = "-fulumaLogStats";
	private const float Interval = 1f;

	private readonly List<(string Label, ProfilerRecorder Recorder, double Sum)> _counters = new();
	private readonly FrameTiming[] _timings = new FrameTiming[1];
	private readonly StringBuilder _text = new();

	private double _cpuSum, _gpuSum, _frameSum;
	private int _timedFrames, _frames;
	private float _elapsed;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void CreateIfRequested()
	{
		if (Application.isEditor)
			return;

		UncapFrameRate();

		if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), LogStatsArgument) < 0)
			return;

		var host = new GameObject(nameof(MobileGpuStats)) { hideFlags = HideFlags.HideAndDontSave };
		DontDestroyOnLoad(host);
		host.AddComponent<MobileGpuStats>();
	}

	/// <summary>
	/// Runs at the display's highest refresh rate: Android defaults to 30 fps, and starts in whatever display mode the
	/// system picked (60 Hz on a 120 Hz Xiaomi 11T), so the frame time would hide the real cost. Picks the fastest mode
	/// at the current size from <see cref="Screen.resolutions"/>, requests it, and sets the target frame rate to it.
	/// </summary>
	private static void UncapFrameRate()
	{
		var best = Screen.currentResolution.refreshRateRatio;
		foreach (var mode in Screen.resolutions)
			if (mode.refreshRateRatio.value > best.value)
				best = mode.refreshRateRatio;

		QualitySettings.vSyncCount = 0;
		Application.targetFrameRate = Mathf.Max(60, Mathf.RoundToInt((float)best.value));

		if (best.value > Screen.currentResolution.refreshRateRatio.value + 0.5)
			Screen.SetResolution(Screen.width, Screen.height, Screen.fullScreenMode, best);
	}

	private void OnEnable()
	{
		var mali = SystemMetricsMali.Instance;
		Debug.Log($"[FulumaGpu] {SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceType}, " +
		          $"{Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F0} Hz " +
		          $"(target {Application.targetFrameRate} fps, modes {ListRefreshRates()}), " +
		          $"Mali counters {(mali != null && mali.Active ? "active" : "unavailable")}");

		if (mali == null || mali.Active == false)
			return;

		var available = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
		mali.GetAvailableCounters(available);
		Debug.Log($"[FulumaGpu] {available.Count} Mali counters available on this device");

		Add("GPU active cy", mali.GpuCycles);
		Add("Frag queue cy", mali.GpuFragmentCycles);
		Add("Non-frag queue cy", mali.GpuVertexAndComputeCycles);
		Add("Tiler cy", mali.GpuTilerCycles);
		Add("Shader frag cy", mali.GpuShaderFragmentCycles);
		Add("ALU issue cy", mali.GpuShaderArithmeticCycles);
		Add("Texture issue cy", mali.GpuShaderTextureCycles);
		Add("Load/store issue cy", mali.GpuShaderLoadStoreCycles);
		Add("Fragment threads", mali.GpuFragmentJobs);
		Add("Pixels", mali.GpuPixels);
		Add("Early-Z kills", mali.GpuEarlyZKills);
		Add("Input primitives", mali.GpuInputPrimitives);
		Add("Visible primitives", mali.GpuVisiblePrimitives);
		Add("Ext read bytes", mali.GpuMemoryReadBytes);
		Add("Ext write bytes", mali.GpuMemoryWriteBytes);
	}

	private static string ListRefreshRates()
	{
		var rates = new SortedSet<int>();
		foreach (var mode in Screen.resolutions)
			rates.Add(Mathf.RoundToInt((float)mode.refreshRateRatio.value));

		return string.Join("/", rates);
	}

	private void Add(string label, Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle handle)
	{
		if (handle.Valid == false)
			return;

		var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
		recorder.Start();
		_counters.Add((label, recorder, 0.0));
	}

	private void OnDisable()
	{
		foreach (var counter in _counters)
			counter.Recorder.Dispose();

		_counters.Clear();
	}

	private void Update()
	{
		FrameTimingManager.CaptureFrameTimings();
		if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
		{
			_cpuSum += _timings[0].cpuFrameTime;
			_gpuSum += _timings[0].gpuFrameTime;
			_timedFrames++;
		}

		for (var i = 0; i < _counters.Count; i++)
		{
			var counter = _counters[i];
			counter.Sum += counter.Recorder.LastValue;
			_counters[i] = counter;
		}

		_frameSum += Time.unscaledDeltaTime * 1000.0;
		_frames++;
		_elapsed += Time.unscaledDeltaTime;

		if (_elapsed >= Interval)
			Flush();
	}

	private void Flush()
	{
		_text.Clear().Append("[FulumaGpu] ");
		_text.AppendFormat("frame {0:F2} ms, cpu {1:F2} ms, gpu {2:F2} ms", _frameSum / _frames,
			_timedFrames > 0 ? _cpuSum / _timedFrames : 0.0, _timedFrames > 0 ? _gpuSum / _timedFrames : 0.0);

		for (var i = 0; i < _counters.Count; i++)
		{
			var counter = _counters[i];
			_text.Append(" | ").Append(counter.Label).Append(' ').AppendFormat("{0:F0}", counter.Sum / _frames);
			counter.Sum = 0.0;
			_counters[i] = counter;
		}

		Debug.Log(_text.ToString());
		_cpuSum = _gpuSum = _frameSum = 0.0;
		_timedFrames = _frames = 0;
		_elapsed = 0f;
	}
}
