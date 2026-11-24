using System;
using System.Collections.Generic;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Core.Lifecycle;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// Base for components that register a fake light in a <see cref="LightStore"/> while enabled.
	/// </summary>
	/// <remarks>
	/// With <see cref="SyncTransform"/> off the light is <b>static</b>: its transform is captured on registration
	/// and changes only through <see cref="ApplyTransform"/>. With it on the light is <b>dynamic</b> and follows
	/// this component's <see cref="Transform"/> every frame.
	/// <para>
	/// While enabled, the component is listed with the package's internal lifecycle helper (<c>FulumaLifecycle</c>),
	/// which registers it again after stores are released by an Editor play-mode switch.
	/// </para>
	/// <para>
	/// <see cref="ClipBoxes"/> limit where the light glows. A box takes effect while it is registered in the same store
	/// as the light; the light applies its boxes again whenever it or one of them (re)registers, so the order they are
	/// enabled in doesn't matter.
	/// </para>
	/// </remarks>
	public abstract class FakeLightComponent : MonoBehaviour, ILightStoreClient
	{
		[Tooltip("Light store to register the light in. When empty, the default store (LightStore.Default) is used.")]
		[SerializeField] private LightStore overrideStore;

		[Tooltip("Follow this Transform every frame (dynamic light). When off, the light is static and moves only when ApplyTransform is called.")]
		[SerializeField] private bool syncTransform;

		[Tooltip("Four slots for boxes limiting where the light glows: Keep boxes (the light exists only inside them, e.g. its room) and Subtract boxes (the light is removed inside them, e.g. a neighbouring room). A slot without a box is unused. Boxes cast no shadows.")]
		[SerializeField] private ClipBoxSlots clipBoxes;

		private LightStore _registeredStore;
		private LightHandle _handle = LightHandle.Null;

		/// <summary>
		/// Boxes whose <see cref="FakeLightClipBox.RegistrationChanged"/> this light listens to; created when the first box
		/// is subscribed to, so lights without clip boxes allocate nothing for it.
		/// </summary>
		private List<FakeLightClipBox> _subscribedBoxes;

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
		/// Whether the light follows this component's <see cref="Transform"/> every frame.
		/// Changing it moves the light to the other pool of the store.
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
		/// The clip boxes this light uses, one per slot. Setting them applies them to the light right away (while it is
		/// enabled); to change one slot, change a copy and assign it back.
		/// </summary>
		public ClipBoxSlots ClipBoxes
		{
			get => clipBoxes;
			set
			{
				if (clipBoxes == value)
					return;

				clipBoxes = value;

				if (isActiveAndEnabled == false)
					return;

				SubscribeToClipBoxes();
				ApplyClipBoxes();
			}
		}

		/// <summary>
		/// The pool the light belongs in: <see cref="LightTransformMode.Dynamic"/> when <see cref="SyncTransform"/> is on,
		/// else <see cref="LightTransformMode.Static"/>.
		/// </summary>
		protected LightTransformMode DesiredMode => syncTransform ? LightTransformMode.Dynamic : LightTransformMode.Static;

		/// <summary>
		/// Whether the light should be in a store at all while this component is enabled (false: it's removed, e.g. while
		/// a linked Unity Light is off or of a type with no fake equivalent).
		/// </summary>
		protected virtual bool WantsRegistration => true;

		private LightStore DesiredStore => overrideStore != null ? overrideStore : LightStore.Default;

		/// <summary>
		/// Copies the current position and rotation into the store. Only needed for static lights; dynamic ones follow
		/// their Transform automatically.
		/// </summary>
		public void ApplyTransform()
		{
			if (_handle.Mode == LightTransformMode.Static && IsRegistered())
				UpdateTransform(_registeredStore, _handle);
		}

		/// <summary>
		/// Pushes serialized light data to the store. Call after changing light parameters from code.
		/// </summary>
		public void ApplyData()
		{
			if (IsRegistered())
				UpdateData(_registeredStore, _handle);
		}

		void ILightStoreClient.RestoreRegistration() => RefreshRegistration();

		/// <summary>
		/// The store and handle of the current registration, if the light is in a store.
		/// </summary>
		/// <returns>True if the light is registered and the handle is still valid.</returns>
		protected internal bool TryGetRegistration(out LightStore store, out LightHandle handle)
		{
			store = _registeredStore;
			handle = _handle;
			return IsRegistered();
		}

		/// <summary>
		/// Whether an existing registration still fits (e.g. its light type is still the right one); false re-registers.
		/// </summary>
		/// <param name="handle">Handle of the current registration.</param>
		protected virtual bool MatchesRegistration(in LightHandle handle) => true;

		/// <summary>
		/// Brings the registration in line with <see cref="WantsRegistration"/>, <see cref="MatchesRegistration"/>, the
		/// store and the transform mode.
		/// </summary>
		/// <returns>True if the light was added, removed or re-registered.</returns>
		protected bool UpdateRegistration() => RefreshRegistration();

		/// <summary>
		/// Adds the light to <paramref name="store"/> in the given mode.
		/// </summary>
		/// <param name="store">Store to add the light to.</param>
		/// <param name="mode">Pool to add it to (static or dynamic).</param>
		/// <returns>Handle of the new light.</returns>
		protected abstract LightHandle Register(LightStore store, LightTransformMode mode);

		/// <summary>
		/// Pushes the serialized light data (color, range, ...) to the store.
		/// </summary>
		/// <param name="store">Store the light is registered in.</param>
		/// <param name="handle">Handle of the light.</param>
		protected abstract void UpdateData(LightStore store, in LightHandle handle);

		/// <summary>
		/// Pushes the current <see cref="Transform"/> of a static light to the store.
		/// </summary>
		/// <param name="store">Store the light is registered in.</param>
		/// <param name="handle">Handle of the light (always a static one).</param>
		protected abstract void UpdateTransform(LightStore store, in LightHandle handle);

		/// <summary>
		/// Registers the light (and its clip boxes) in the store. Overrides must call the base method.
		/// </summary>
		protected virtual void OnEnable()
		{
			FulumaLifecycle.AddClient(this);
			SubscribeToClipBoxes();
			RefreshRegistration();
		}

		/// <summary>
		/// Removes the light from its store. Overrides must call the base method.
		/// </summary>
		protected virtual void OnDisable()
		{
			FulumaLifecycle.RemoveClient(this);
			UnsubscribeFromClipBoxes();
			Unregister();
		}

#if UNITY_EDITOR
		/// <summary>
		/// Editor only: pushes Inspector changes (store, mode, data, placement, clip boxes) to the store. Overrides must
		/// call the base method.
		/// </summary>
		protected virtual void OnValidate()
		{
			if (isActiveAndEnabled == false || _registeredStore == null)
				return;

			SubscribeToClipBoxes();

			if (RefreshRegistration())
				return;

			// Same store and mode: only the data (or, for static lights, the placement, or the boxes) may have changed.
			UpdateData(_registeredStore, _handle);

			if (_handle.Mode == LightTransformMode.Static)
				UpdateTransform(_registeredStore, _handle);

			ApplyClipBoxes();
		}
#endif

		private bool IsRegistered()
		{
			return _registeredStore != null && _registeredStore.Contains(_handle);
		}

		/// <summary>
		/// Brings the registration in line with the desired store and mode.
		/// </summary>
		/// <returns>True if the light was (re)registered.</returns>
		private bool RefreshRegistration()
		{
			if (isActiveAndEnabled == false)
				return false;

			if (WantsRegistration == false)
			{
				var wasRegistered = _handle.IsNull == false;
				Unregister();
				return wasRegistered;
			}

			var store = DesiredStore;

			if (store == _registeredStore && _handle.Mode == DesiredMode && IsRegistered() && MatchesRegistration(_handle))
				return false;

			Unregister();

			_registeredStore = store;
			_handle = Register(store, DesiredMode);
			ApplyClipBoxes();
			return true;
		}

		/// <summary>
		/// Pushes the boxes of <see cref="ClipBoxes"/> that are registered in the light's store; the others are skipped
		/// until they register (see <see cref="OnClipBoxRegistrationChanged"/>).
		/// </summary>
		private void ApplyClipBoxes()
		{
			if (IsRegistered() == false)
				return;

			WarnAboutClipBoxesInOtherStores();
			_registeredStore.SetClipBoxes(_handle, clipBoxes);
		}

		private void WarnAboutClipBoxesInOtherStores()
		{
			for (var i = 0; i < ClipBoxSlots.Count; i++)
			{
				var box = clipBoxes[i].Box;
				if (box != null && box.TryGetRegistration(out var boxStore, out _) && boxStore != _registeredStore)
				{
					Debug.LogWarning(Utils.StringUtils.PrependFulumaPrefix(
						$"'{name}' uses clip box '{box.name}' from another light store; it is ignored."), this);
				}
			}
		}

		private void OnClipBoxRegistrationChanged(FakeLightClipBox box) => ApplyClipBoxes();

		private void SubscribeToClipBoxes()
		{
			UnsubscribeFromClipBoxes();

			for (var i = 0; i < ClipBoxSlots.Count; i++)
			{
				var box = clipBoxes[i].Box;
				if (box == null || (_subscribedBoxes != null && _subscribedBoxes.Contains(box)))
					continue;

				box.RegistrationChanged += OnClipBoxRegistrationChanged;
				_subscribedBoxes ??= new List<FakeLightClipBox>(ClipBoxSlots.Count);
				_subscribedBoxes.Add(box);
			}
		}

		private void UnsubscribeFromClipBoxes()
		{
			if (_subscribedBoxes == null)
				return;

			foreach (var box in _subscribedBoxes)
			{
				if (box != null)
					box.RegistrationChanged -= OnClipBoxRegistrationChanged;
			}

			_subscribedBoxes.Clear();
		}

		private void Unregister()
		{
			var store = _registeredStore;
			var handle = _handle;

			_registeredStore = null;
			_handle = LightHandle.Null;

			// A released store already dropped the light; Remove just returns false then.
			if (store != null && handle.IsNull == false)
				store.Remove(handle);
		}
	}
}
