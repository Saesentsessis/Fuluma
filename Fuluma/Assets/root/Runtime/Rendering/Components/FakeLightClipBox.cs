using System;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Core.Lifecycle;
using Saesentsessis.Fuluma.Data;
using UnityEngine;
using UnityEngine.Serialization;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// An oriented box that limits where fake lights glow. Lights list the boxes they use (see
	/// <see cref="FakeLightComponent.ClipBoxes"/>), each as Keep (the light exists only inside its keep boxes, for example
	/// its room) or Subtract (the light is removed inside the box, for example a neighbouring room). Boxes don't cast
	/// shadows.
	/// </summary>
	/// <remarks>
	/// The box is <see cref="Shape"/>'s center and size in this <see cref="Transform"/>'s space: rotation and scale (even
	/// non-uniform, under a rotated parent) apply. Like the light components it is static, placed when it registers and
	/// moved by <see cref="ApplyTransform"/>, or, with <see cref="SyncTransform"/>, dynamic and following the Transform
	/// every frame. A light uses a box only while both are registered in the same store.
	/// </remarks>
	[ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("Light/Volumetric/Fake Light Clip Box")]
	public class FakeLightClipBox : MonoBehaviour, ILightStoreClient
	{
		/// <summary>
		/// Raised whenever the box is added to, removed from or re-added to a store (new handle), so the lights using it
		/// can apply it again.
		/// </summary>
		internal event Action<FakeLightClipBox> RegistrationChanged;

		[Tooltip("Light store to register the box in. When empty, the default store (LightStore.Default) is used. Only lights in the same store can use the box.")]
		[SerializeField] private LightStore overrideStore;

		[Tooltip("Follow this Transform every frame (dynamic box). When off, the box is static and moves only when ApplyTransform is called.")]
		[SerializeField] private bool syncTransform;

		[Tooltip("Center and size of the box in this Transform's space (rotation and scale apply).")]
		[SerializeField] private ClipBoxData shape = ClipBoxData.Default;

		private LightStore _registeredStore;
		private ClipBoxHandle _handle = ClipBoxHandle.Null;

		/// <summary>
		/// Store to register in. <see cref="LightStore.Default"/> is used when null.
		/// </summary>
		public LightStore OverrideStore
		{
			get => overrideStore;
			set
			{
				if (overrideStore == value)
					return;

				overrideStore = value;
				RefreshRegistration();
			}
		}

		/// <summary>
		/// Whether the box follows this component's <see cref="Transform"/> every frame.
		/// </summary>
		public bool SyncTransform
		{
			get => syncTransform;
			set
			{
				if (syncTransform == value)
					return;

				syncTransform = value;
				RefreshRegistration();
			}
		}

		/// <summary>
		/// Center and size in this Transform's space. Setting it pushes the change to the store.
		/// </summary>
		public ClipBoxData Shape
		{
			get => shape;
			set
			{
				shape = value;
				if (TryGetRegistration(out var store, out var handle))
					store.SetClipBoxShape(handle, shape);
			}
		}

		private LightStore DesiredStore => overrideStore != null ? overrideStore : LightStore.Default;

		private LightTransformMode DesiredMode => syncTransform ? LightTransformMode.Dynamic : LightTransformMode.Static;

		/// <summary>
		/// The store and handle of the current registration, if the box is in a store.
		/// </summary>
		/// <param name="store">The store the box is registered in, or null.</param>
		/// <param name="handle">The box's handle in that store.</param>
		/// <returns>True if the box is registered and the handle is still valid.</returns>
		public bool TryGetRegistration(out LightStore store, out ClipBoxHandle handle)
		{
			store = _registeredStore;
			handle = _handle;
			return store != null && store.Contains(handle);
		}

		/// <summary>
		/// Copies the Transform's current placement into the store. Only needed for static boxes; dynamic ones follow
		/// their Transform automatically.
		/// </summary>
		public void ApplyTransform()
		{
			if (_handle.Mode == LightTransformMode.Static && TryGetRegistration(out var store, out var handle))
				store.SetClipBoxPlacement(handle, transform.localToWorldMatrix);
		}

		void ILightStoreClient.RestoreRegistration() => RefreshRegistration();

		private void OnEnable()
		{
			FulumaLifecycle.AddClient(this);
			RefreshRegistration();
		}

		private void OnDisable()
		{
			FulumaLifecycle.RemoveClient(this);

			if (Unregister())
				RegistrationChanged?.Invoke(this);
		}

#if UNITY_EDITOR
		private void OnValidate()
		{
			if (isActiveAndEnabled == false || _registeredStore == null)
				return;

			if (RefreshRegistration())
				return;

			// Same store and mode: only the shape (or, for a static box, the placement) may have changed.
			_registeredStore.SetClipBoxShape(_handle, shape);
			ApplyTransform();
		}
#endif

		/// <summary>
		/// Brings the registration in line with the desired store and mode.
		/// </summary>
		/// <returns>True if the box was (re)registered.</returns>
		private bool RefreshRegistration()
		{
			if (isActiveAndEnabled == false)
				return false;

			var store = DesiredStore;
			if (store == _registeredStore && _handle.Mode == DesiredMode && TryGetRegistration(out _, out _))
				return false;

			Unregister();

			_registeredStore = store;
			_handle = DesiredMode == LightTransformMode.Dynamic
				? store.AddClipBox(transform, shape)
				: store.AddClipBox(transform.localToWorldMatrix, shape);

			RegistrationChanged?.Invoke(this);
			return true;
		}

		/// <returns>Whether the box was registered.</returns>
		private bool Unregister()
		{
			var store = _registeredStore;
			var handle = _handle;

			_registeredStore = null;
			_handle = ClipBoxHandle.Null;

			// A released store already dropped the box (and every light's reference to it); Remove just returns false.
			if (store == null || handle.IsNull)
				return false;

			store.Remove(handle);
			return true;
		}
	}
}
