#if UNITY_6000_5_OR_NEWER && !FULUMA_LEGACY_LIFECYCLE
#define FULUMA_LIFECYCLE_ATTRIBUTES
#endif
using System;
using System.Collections.Generic;
using Saesentsessis.Fuluma.Core.Pools;
using Saesentsessis.Fuluma.Data;
using Saesentsessis.Fuluma.Rendering.Components;
using Unity.Collections;
using Unity.Jobs;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif
using UnityEngine;
using UnityEngine.Jobs;

namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// A collection of registered fake lights and the clip boxes that limit them.
	/// </summary>
	/// <remarks>
	/// Every light type is split into two pools by where its transform comes from:
	/// <list type="bullet">
	/// <item><b>Static</b> — transform is set from code (on add and through explicit updates).</item>
	/// <item><b>Dynamic</b> — transform is read from a <see cref="Transform"/> every frame.</item>
	/// </list>
	/// Each pool keeps transforms and light data at the same dense index. A <see cref="LightHandle"/>
	/// records its pool, so one handle is all a caller needs. Native memory (lights and clip boxes) is allocated on the
	/// first add and released with the store.
	/// <para>
	/// Play-mode switches are handled by <c>FulumaLifecycle</c>: every store is released when a mode is
	/// left, and every enabled fake light registers again once the new mode is entered.
	/// </para>
	/// <para>
	/// Files: this one holds every constant and field, the lifecycle and the methods for any light type (which route a
	/// handle to its type's private implementation);
	/// <c>SpotLights</c> and <c>ClipBoxes</c> the public API of each; <c>Jobs</c> the dynamic transform syncs, the
	/// buffer copies and the culling readers; <c>Rendering</c> what the GPU mirror and the culler read.
	/// </para>
	/// </remarks>
#if UNITY_6000_5_OR_NEWER
	// Stores, the default store and the epoch counter must survive play-mode switches (see FulumaLifecycle).
	[NoAutoStaticsCleanup]
#endif
	[CreateAssetMenu(fileName = "New Light Store", menuName = "Saesentsessis/Fuluma/Light Store")]
	public partial class LightStore : ScriptableObject
	{
		/// <summary>
		/// Most clip boxes one store holds (a light's reference stores a 15-bit box index).
		/// </summary>
		public const int MaxClipBoxes = ClipBoxArray.MaxBoxes;

		/// <summary>
		/// Most clip boxes one light references.
		/// </summary>
		public const int MaxClipBoxesPerLight = PackedClipBoxReferences.Count;

		private const Allocator StoreAllocator = Allocator.Persistent;
		private const int InitialSpotLightCapacity = 64;
		private const int InitialClipBoxCapacity = 16;
		private const int DynamicTransformBatchSize = 64;
		private const int DynamicClipBoxBatchSize = 32;

		// Statics below must survive play-mode switches without a domain reload, so none of them is reset.

		/// <summary>
		/// Every store that currently owns native memory.
		/// </summary>
		private static readonly HashSet<LightStore> AllocatedStores = new();

		private static LightStore _default;

		/// <summary>
		/// Source of epochs for every store. Shared so that no two stores (or two allocations of one store)
		/// ever hold the same epoch, which makes a handle from one store fail validation in any other.
		/// Must survive play-mode transitions: a reset could hand out an epoch that a stale handle still carries.
		/// </summary>
		private static uint _lastEpoch;

		/// <summary>
		/// The shared store used when none is assigned: fake light components and clip boxes register in it, and the
		/// render feature draws it, unless their override store is set. Created on first access as a
		/// <see cref="HideFlags.DontSave"/> instance.
		/// </summary>
		public static LightStore Default
		{
			get
			{
				if (_default != null)
					return _default;

				_default = CreateInstance<LightStore>();
				_default.name = "Default Light Store";
				_default.hideFlags = HideFlags.DontSave;
				return _default;
			}
		}

		/// <summary>
		/// Frees the native memory of every store. All handles die; stores allocate again on the next add.
		/// </summary>
		internal static void ReleaseAll()
		{
			foreach (var store in AllocatedStores)
				store.ReleaseNative(untrack: false);

			AllocatedStores.Clear();
		}

#if FULUMA_LIFECYCLE_ATTRIBUTES
		[OnCodeUnloading]
		private static void ReleaseAllOnCodeUnload() => ReleaseAll();
#elif UNITY_EDITOR
		// Before 6000.5: release on code reload explicitly rather than relying on every store's OnDisable during the
		// domain unload (the default store is a DontSave instance).
		[UnityEditor.InitializeOnLoadMethod]
		private static void SubscribeToCodeReload()
		{
			UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
			UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
		}
#endif

		private static uint NextEpoch()
		{
			// 0 is reserved for LightHandle.Null.
			if (++_lastEpoch == 0)
				_lastEpoch = 1;

			return _lastEpoch;
		}

		/// <summary>
		/// Removes every transform, keeping the array's memory.
		/// </summary>
		private static void ClearTransforms(TransformAccessArray transforms)
			=> transforms.SetTransforms(Array.Empty<Transform>());

		private static void DisposeTransforms(ref TransformAccessArray transforms)
		{
			if (transforms.isCreated)
				transforms.Dispose();

			transforms = default;
		}

		[NonSerialized] private uint _epoch;
		[NonSerialized] private bool _allocated;

		[NonSerialized] private SpotLightPool _staticSpotLights;
		[NonSerialized] private SpotLightPool _dynamicSpotLights;

		/// <summary>
		/// Mirrors the dense order of <see cref="_dynamicSpotLights"/>.
		/// </summary>
		[NonSerialized] private TransformAccessArray _dynamicSpotLightTransforms;

		/// <summary>
		/// Pending <see cref="PackDynamicTransformsJob"/> (and the copy of its output); it writes into the dynamic pool's
		/// transform and culling lists.
		/// </summary>
		[NonSerialized] private JobHandle _dynamicSpotLightSync;

		/// <summary>
		/// Pending jobs that read the pools (culling). Every change to a pool waits for them first.
		/// </summary>
		[NonSerialized] private JobHandle _spotLightReaders;

		[NonSerialized] private ClipBoxArray _clipBoxes;

		/// <summary>
		/// Mirrors the dense order of <see cref="ClipBoxArray.DynamicBoxes"/>.
		/// </summary>
		[NonSerialized] private TransformAccessArray _dynamicClipBoxTransforms;

		/// <summary>
		/// Pending <see cref="PackDynamicClipBoxesJob"/> (and the copy of the boxes); it writes into the box array.
		/// </summary>
		[NonSerialized] private JobHandle _dynamicClipBoxSync;

		/// <summary>
		/// Indices of removed boxes that lights may still reference. Dropped from the lights in one pass, as late as
		/// possible: before the references are read for the GPU, or before a new box could reuse an index
		/// (<see cref="FlushRemovedClipBoxReferences"/>).
		/// </summary>
		[NonSerialized] private NativeList<int> _removedClipBoxes;

		/// <summary>
		/// Generation of the store's native memory. Replaced with a new, globally unique value whenever the memory is
		/// allocated or released; handles issued under any other epoch are dead.
		/// </summary>
		internal uint Epoch => _epoch;

		/// <summary>
		/// Whether native memory is currently allocated (lazily, on the first add).
		/// </summary>
		internal bool IsAllocated => _allocated;

		/// <summary>
		/// Whether the handle refers to a light that is still registered in this store.
		/// </summary>
		/// <param name="handle">A handle of any light type.</param>
		public bool Contains(in LightHandle handle) => handle.Type switch
		{
			FakeLightType.Spot => TryGetSpotLightIndex(handle, out _),
			_ => false
		};

		/// <summary>
		/// Removes a light of any type.
		/// </summary>
		/// <returns>False if the handle was already dead.</returns>
		public bool Remove(in LightHandle handle) => handle.Type switch
		{
			FakeLightType.Spot => RemoveSpotLight(handle),
			_ => false
		};

		/// <summary>
		/// Sets the clip boxes a light uses, replacing its previous ones. An empty span removes them all.
		/// </summary>
		/// <param name="light">A light of this store.</param>
		/// <param name="boxes">Up to <see cref="MaxClipBoxesPerLight"/> boxes of this store, in any order.</param>
		/// <returns>False, and nothing changed, if the light or any box handle is dead (a box of another store counts
		/// as dead).</returns>
		/// <exception cref="ArgumentException">More than <see cref="MaxClipBoxesPerLight"/> boxes.</exception>
		public bool SetClipBoxes(in LightHandle light, ReadOnlySpan<ClipBoxReference> boxes) => light.Type switch
		{
			FakeLightType.Spot => SetSpotLightClipBoxes(light, boxes),
			_ => false
		};

		/// <summary>
		/// Sets the clip boxes a light uses from a component's <see cref="ClipBoxSlots"/>: the box of every used slot,
		/// with its role, while that box is registered in this store. Boxes that aren't (disabled, or in another store)
		/// are skipped.
		/// </summary>
		/// <param name="light">A light of this store.</param>
		/// <param name="slots">The slots to read.</param>
		/// <returns>False, and nothing changed, if the light is dead.</returns>
		public bool SetClipBoxes(in LightHandle light, in ClipBoxSlots slots)
		{
			Span<ClipBoxReference> references = stackalloc ClipBoxReference[ClipBoxSlots.Count];
			var count = 0;

			for (var i = 0; i < ClipBoxSlots.Count; i++)
			{
				var slot = slots[i];
				if (slot.Box != null && slot.Box.TryGetRegistration(out var store, out var box) && store == this)
					references[count++] = new ClipBoxReference(box, slot.Role);
			}

			return SetClipBoxes(light, references[..count]);
		}

		/// <summary>
		/// Copies the clip boxes a light uses into <paramref name="output"/>.
		/// </summary>
		/// <param name="light">A light of this store.</param>
		/// <param name="output">At least <see cref="MaxClipBoxesPerLight"/> long to be sure to get all of them. Boxes
		/// removed since they were set are skipped.</param>
		/// <param name="count">How many were written.</param>
		/// <returns>False if the light handle is dead.</returns>
		public bool TryGetClipBoxes(in LightHandle light, Span<ClipBoxReference> output, out int count)
		{
			if (light.Type == FakeLightType.Spot)
				return TryGetSpotLightClipBoxes(light, output, out count);

			count = 0;
			return false;
		}

		/// <summary>
		/// Removes every fake light of every type and every clip box, invalidating all previously issued
		/// <see cref="LightHandle"/>s and <see cref="ClipBoxHandle"/>s. Native memory stays allocated.
		/// </summary>
		public void Clear()
		{
			ClearSpotLights();
			ClearClipBoxes();
		}

		private ref SpotLightPool PoolOf(LightTransformMode mode)
		{
			if (mode == LightTransformMode.Static)
				return ref _staticSpotLights;

			return ref _dynamicSpotLights;
		}

		private void EnsureAllocated()
		{
			if (_allocated)
				return;

			AllocateSpotLights();
			AllocateClipBoxes();

			_allocated = true;
			_epoch = NextEpoch();

			AllocatedStores.Add(this);
		}

		/// <summary>
		/// Frees all native memory. Every handle issued so far becomes dead.
		/// </summary>
		/// <param name="untrack">Remove the store from <see cref="AllocatedStores"/>. False only while <see cref="ReleaseAll"/> iterates it.</param>
		private void ReleaseNative(bool untrack)
		{
			if (_allocated == false)
				return;

			ReleaseSpotLights();
			ReleaseClipBoxes();

			_allocated = false;
			_epoch = NextEpoch();

			if (untrack)
				AllocatedStores.Remove(this);
		}

		private void OnDisable() => ReleaseNative(untrack: true);
	}
}
