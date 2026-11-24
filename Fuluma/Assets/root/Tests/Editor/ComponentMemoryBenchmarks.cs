using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Saesentsessis.Fuluma.Rendering.Components;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Saesentsessis.Fuluma.Tests.Editor
{
	/// <summary>
	/// Managed memory of the components: the object's field layout (from the runtime's field offsets), and the managed
	/// heap growth measured for many components, inactive (the object alone) and active (plus what registering
	/// allocates). Runs only with <c>FULUMA_BENCHMARK</c> set.
	/// </summary>
	public class ComponentMemoryBenchmarks
	{
		private const string BenchmarkVariable = "FULUMA_BENCHMARK";
		private const int Count = 10000;

		private static readonly Type[] ComponentTypes = { typeof(FakeSpotLight), typeof(LinkedFakeLight), typeof(FakeLightClipBox) };

		private static string Layout(Type type, out int objectSize)
		{
			var fields = new List<(int Offset, int Size, string Text)>();
			for (var current = type; current != null && current != typeof(object); current = current.BaseType)
			{
				foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
				                                        BindingFlags.DeclaredOnly))
				{
					var size = field.FieldType.IsValueType ? UnsafeUtility.SizeOf(field.FieldType) : IntPtr.Size;
					fields.Add((UnsafeUtility.GetFieldOffset(field), size,
						$"{current.Name}.{field.Name} ({TypeName(field.FieldType)})"));
				}
			}

			fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
			objectSize = 0;
			var text = new StringBuilder();
			foreach (var (offset, size, name) in fields)
			{
				text.AppendLine($"    {offset,4} +{size,3}  {name}");
				objectSize = Math.Max(objectSize, offset + size);
			}

			// The runtime's allocations are 8-byte aligned.
			objectSize = (objectSize + 7) & ~7;
			return text.ToString();
		}

		private static string TypeName(Type type)
			=> type.IsGenericType ? $"{type.Name.Split('`')[0]}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), TypeName))}>" : type.Name;

		private static long ManagedHeap()
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			return GC.GetTotalMemory(true);
		}

		private static void Measure(Type type, out double inactive, out double active)
		{
			var objects = new GameObject[Count];
			var components = new Component[Count];
			try
			{
				for (var i = 0; i < Count; i++)
				{
					objects[i] = new GameObject($"Memory {i}") { hideFlags = HideFlags.HideAndDontSave };
					objects[i].SetActive(false);
					if (type == typeof(LinkedFakeLight))
						objects[i].AddComponent<Light>().type = LightType.Spot;
				}

				var before = ManagedHeap();
				for (var i = 0; i < Count; i++)
					components[i] = objects[i].AddComponent(type);

				var afterAdd = ManagedHeap();
				for (var i = 0; i < Count; i++)
					objects[i].SetActive(true);

				var afterEnable = ManagedHeap();
				inactive = (afterAdd - before) / (double)Count;
				active = (afterEnable - before) / (double)Count;
				GC.KeepAlive(components);
			}
			finally
			{
				for (var i = 0; i < Count; i++)
				{
					if (objects[i] != null)
						Object.DestroyImmediate(objects[i]);
				}
			}
		}

		[Test]
		public void Benchmark_ComponentManagedMemory()
		{
			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BenchmarkVariable)))
				Assert.Ignore($"Set {BenchmarkVariable}=1 to run the benchmark.");

			var report = new StringBuilder();
			report.AppendLine($"BENCHMARK component managed memory ({IntPtr.Size * 8}-bit, {Count} components per measurement)");

			foreach (var type in ComponentTypes)
			{
				var layout = Layout(type, out var objectSize);
				report.AppendLine($"{type.Name}: object {objectSize} B (offset +size, including the object header)");
				report.Append(layout);

				Measure(type, out var inactive, out var active);
				report.AppendLine($"  measured per component: {inactive:F1} B inactive (object and what its constructor allocates), " +
				                  $"{active:F1} B once enabled (plus registration)");
			}

			TestContext.WriteLine(report.ToString());
		}
	}
}
