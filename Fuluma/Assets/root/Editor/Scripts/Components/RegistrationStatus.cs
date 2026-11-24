using System.Collections.Generic;
using System.Text;
using Saesentsessis.Fuluma.Core;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Fuluma.Editor.Components
{
	/// <summary>
	/// The status box at the top of a fake light's or clip box's Inspector: whether the component is registered, in which
	/// store and pool (or why not), and which renderers draw that store, with a warning when none does, when a quality
	/// level doesn't, or (for clip boxes) when the drawing feature has clip boxes off.
	/// </summary>
	/// <remarks>
	/// Built only on Layout events (so Layout and Repaint see the same text and height), and only when the registration
	/// changed or half a second passed since the last build, which picks up renderer and quality setting edits. Between
	/// builds a repaint costs one help box.
	/// </remarks>
	internal sealed class RegistrationStatus
	{
		private const double RefreshInterval = 0.5;

		internal static readonly Reason PrefabAsset = new(
			"Prefab asset: registers when an instance is enabled in a scene.", MessageType.Info);

		internal static readonly Reason Disabled = new(
			"Not registered: the component or its GameObject is disabled.", MessageType.Info);

		internal static readonly Reason Unknown = new("Not registered.", MessageType.Warning);

		private readonly StoreFeatureLookup.Result _lookup = new();
		private readonly StringBuilder _builder = new();

		private string _message = string.Empty;
		private MessageType _type;
		private double _nextRefresh;

		// What the current message was built from.
		private bool _builtRegistered;
		private LightStore _builtStore;
		private LightTransformMode _builtMode;
		private string _builtReason;

		/// <summary>
		/// Draws the status box (layout version).
		/// </summary>
		/// <param name="subject">Light or clip box.</param>
		/// <param name="overrideStore">The component's override store (null: the default store).</param>
		/// <param name="registered">Whether the component is registered now.</param>
		/// <param name="store">The store it is registered in, when registered.</param>
		/// <param name="mode">Its pool, when registered.</param>
		/// <param name="reason">Why it isn't registered, when it isn't.</param>
		public void Draw(Subject subject, LightStore overrideStore, bool registered, LightStore store, LightTransformMode mode,
			in Reason reason)
		{
			if (Event.current.type == EventType.Layout && NeedsRefresh(overrideStore, registered, store, mode, reason))
				Build(subject, overrideStore, registered, store, mode, reason);

			EditorGUILayout.HelpBox(_message, _type);
		}

		private bool NeedsRefresh(LightStore overrideStore, bool registered, LightStore store, LightTransformMode mode,
			in Reason reason)
		{
			var shownStore = registered ? store : overrideStore;
			return EditorApplication.timeSinceStartup >= _nextRefresh
			       || registered != _builtRegistered
			       || shownStore != _builtStore
			       || (registered && mode != _builtMode)
			       || (registered == false && ReferenceEquals(reason.Text, _builtReason) == false);
		}

		private void Build(Subject subject, LightStore overrideStore, bool registered, LightStore store, LightTransformMode mode,
			in Reason reason)
		{
			_nextRefresh = EditorApplication.timeSinceStartup + RefreshInterval;
			_builtRegistered = registered;
			_builtStore = registered ? store : overrideStore;
			_builtMode = mode;
			_builtReason = reason.Text;

			var storeName = registered && store != null ? store.name
				: overrideStore != null ? overrideStore.name
				: "the default light store";

			_builder.Clear();
			_type = MessageType.Info;

			AppendRegistration(subject, registered, storeName, mode, reason);
			_builder.Append('\n');

			StoreFeatureLookup.Find(overrideStore, _lookup);
			AppendDrawing(subject, overrideStore == null, storeName);

			_message = _builder.ToString();
		}

		private void AppendRegistration(Subject subject, bool registered, string storeName, LightTransformMode mode,
			in Reason reason)
		{
			if (registered == false)
			{
				_builder.Append(reason.Text);
				Raise(reason.Type);
				return;
			}

			var dynamic = mode == LightTransformMode.Dynamic;
			_builder.Append("Registered in '").Append(storeName).Append("' as a ")
				.Append(dynamic ? "dynamic " : "static ")
				.Append(subject == Subject.Light ? "light" : "clip box")
				.Append(dynamic ? " (follows its Transform every frame)." : " (moves on ApplyTransform).");
		}

		private void AppendDrawing(Subject subject, bool defaultStore, string storeName)
		{
			if (_lookup.AnyUniversalPipeline == false)
			{
				_builder.Append("No quality level renders with URP: Fuluma draws only in the Universal Render Pipeline.");
				Raise(MessageType.Warning);
				return;
			}

			if (_lookup.DrawingRenderers.Count == 0)
			{
				_builder.Append("No active Fuluma Render Feature draws '").Append(storeName).Append("', so ")
					.Append(subject == Subject.Light ? "this light is invisible." : "this box has no effect.")
					.Append(" Add the feature to a Universal Renderer and ")
					.Append(defaultStore ? "leave its Override Store empty." : "set its Override Store to this store.");
				Raise(MessageType.Warning);
				AppendList(" Disabled Fuluma feature on: ", _lookup.DisabledRenderers);
				return;
			}

			AppendList("Drawn by: ", _lookup.DrawingRenderers);

			if (_lookup.MissingLevels.Count > 0)
			{
				AppendList(_lookup.MissingLevels.Count == 1 ? " Not drawn on quality level " : " Not drawn on quality levels ",
					_lookup.MissingLevels);
				Raise(MessageType.Warning);
			}

			AppendList(" Disabled Fuluma feature on: ", _lookup.DisabledRenderers);

			if (subject == Subject.ClipBox && _lookup.NoClipBoxRenderers.Count > 0)
			{
				AppendList(" Clip Boxes is off in the feature's Fidelity Features on: ", _lookup.NoClipBoxRenderers);
				_builder.Insert(_builder.Length - 1, " (no effect there)");
				Raise(MessageType.Warning);
			}
		}

		/// <summary>
		/// Appends "prefix a, b, c." (nothing for an empty list).
		/// </summary>
		private void AppendList(string prefix, List<string> names)
		{
			if (names.Count == 0)
				return;

			_builder.Append(prefix);

			for (var i = 0; i < names.Count; i++)
			{
				if (i > 0)
					_builder.Append(", ");

				_builder.Append(names[i]);
			}

			_builder.Append('.');
		}

		private void Raise(MessageType type)
		{
			if (type > _type)
				_type = type;
		}

		/// <summary>
		/// What the component is, for the wording.
		/// </summary>
		internal enum Subject : byte
		{
			Light,
			ClipBox
		}

		/// <summary>
		/// Why a component isn't registered, and how serious that is.
		/// </summary>
		internal readonly struct Reason
		{
			public readonly string Text;
			public readonly MessageType Type;

			public Reason(string text, MessageType type)
			{
				Text = text;
				Type = type;
			}
		}
	}
}
