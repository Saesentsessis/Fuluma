using System;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Editor.Drawing
{
	/// <summary>
	/// A structural change a <see cref="FulumaGui.LodBar"/> asks its caller for, from its context menu.
	/// </summary>
	internal enum LodBarCommand : byte
	{
		None,

		/// <summary>
		/// Insert a LOD before the one at the command's index, taking the upper half of its range.
		/// </summary>
		InsertBefore,

		/// <summary>
		/// Delete the LOD at the command's index.
		/// </summary>
		Delete
	}

#if UNITY_6000_5_OR_NEWER
	// Colors and labels are immutable or caches; the pending menu command is consumed by the next bar laid out.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Shared IMGUI widgets of the package's editors.
	/// </summary>
	internal static class FulumaGui
	{
		/// <summary>
		/// Pixels on either side of a boundary that grab it.
		/// </summary>
		private const float BoundaryGrabWidth = 4f;

		/// <summary>
		/// LODs whose labels are cached; further LODs are drawn unlabeled.
		/// </summary>
		private const int LabelCacheSize = 16;

		private const float SelectedLightening = 0.25f;

		private static readonly int LodBarHint = "FulumaLodBar".GetHashCode();

		private static readonly Color[] LodColors =
		{
			new(0.38f, 0.49f, 0.18f),
			new(0.24f, 0.39f, 0.46f),
			new(0.18f, 0.29f, 0.48f),
			new(0.33f, 0.24f, 0.48f),
			new(0.45f, 0.2f, 0.32f),
			new(0.5f, 0.31f, 0.14f),
			new(0.46f, 0.43f, 0.14f),
			new(0.29f, 0.29f, 0.29f)
		};

		/// <summary>
		/// <see cref="LodColors"/> lightened, for the selected LOD.
		/// </summary>
		private static readonly Color[] SelectedLodColors = Lightened(LodColors, SelectedLightening);

		private static readonly Color SelectedOutline = new(1f, 1f, 1f, 0.9f);
		private static readonly Color Background = new(0.12f, 0.12f, 0.12f);

		private static readonly GUIContent InsertBeforeLabel = new("Insert Before");
		private static readonly GUIContent DeleteLabel = new("Delete");

		/// <summary>
		/// Labels per LOD index and the range each shows (NaN until built); a label is rebuilt only when its range changes.
		/// </summary>
		private static readonly GUIContent[] LodLabels = new GUIContent[LabelCacheSize];
		private static readonly float[] LodLabelTops = Filled(LabelCacheSize, float.NaN);
		private static readonly float[] LodLabelBottoms = Filled(LabelCacheSize, float.NaN);

		private static LodBarCommand _pendingCommand;
		private static int _pendingIndex;

		/// <summary>
		/// The boundary being dragged (index of the LOD above it), or -1.
		/// </summary>
		private static int _draggedBoundary = -1;

		/// <summary>
		/// A LOD bar like Unity's LODGroup editor: one segment per LOD from 100% of the screen height (left) to 0%
		/// (right), on a square-root scale so small screen sizes get room, with draggable boundaries.
		/// </summary>
		/// <remarks>
		/// <paramref name="screenSizes"/> holds one threshold per LOD, non-increasing: LOD <c>i</c> spans from the previous
		/// LOD's threshold (100% for LOD0) down to its own; the last LOD spans down to 0% whatever its threshold. Dragging
		/// the boundary below LOD <c>i</c> edits <c>screenSizes[i]</c>, kept between its neighbours. A click selects a
		/// segment; segments are labeled like LODGroup's, with the percentage their LOD starts at (100% for LOD0). A
		/// right-click offers Insert Before and Delete, reported through <paramref name="command"/> on the next
		/// Layout event (structural changes are the caller's, which owns the storage). No allocations except for the
		/// context menu and a label whose value changed.
		/// </remarks>
		/// <param name="position">Where to draw the bar.</param>
		/// <param name="screenSizes">LOD thresholds as fractions of the screen height; edited in place.</param>
		/// <param name="selected">The selected LOD; changed by clicks and drags.</param>
		/// <param name="maxCount">Most LODs; Insert Before is disabled at it.</param>
		/// <param name="command">A context-menu request, or <see cref="LodBarCommand.None"/>.</param>
		/// <param name="commandIndex">The LOD the command applies to, or -1.</param>
		/// <returns>Whether any threshold changed.</returns>
		internal static bool LodBar(Rect position, Span<float> screenSizes, ref int selected, int maxCount,
			out LodBarCommand command, out int commandIndex)
		{
			var current = Event.current;
			TakePendingCommand(current, out command, out commandIndex);

			var count = screenSizes.Length;
			if (count == 0)
				return false;

			selected = math.clamp(selected, 0, count - 1);

			var id = GUIUtility.GetControlID(LodBarHint, FocusType.Passive, position);
			var changed = false;

			switch (current.GetTypeForControl(id))
			{
				case EventType.Repaint:
					DrawLodBar(position, screenSizes, selected);
					AddBoundaryCursors(position, screenSizes);
					break;

				case EventType.MouseDown when current.button == 0 && position.Contains(current.mousePosition):
				{
					var boundary = BoundaryAt(position, screenSizes, current.mousePosition.x);
					if (boundary >= 0)
					{
						GUIUtility.hotControl = id;
						_draggedBoundary = boundary;
						selected = boundary;
					}
					else
					{
						selected = SegmentAt(position, screenSizes, current.mousePosition.x);
					}

					current.Use();
					break;
				}

				case EventType.MouseDrag when GUIUtility.hotControl == id && _draggedBoundary >= 0:
					changed = DragBoundary(position, screenSizes, _draggedBoundary, current.mousePosition.x);
					current.Use();
					break;

				case EventType.MouseUp when GUIUtility.hotControl == id:
					GUIUtility.hotControl = 0;
					_draggedBoundary = -1;
					current.Use();
					break;

				case EventType.ContextClick when position.Contains(current.mousePosition):
					selected = SegmentAt(position, screenSizes, current.mousePosition.x);
					ShowLodMenu(selected, count, maxCount);
					current.Use();
					break;
			}

			if (changed)
				GUI.changed = true;

			return changed;
		}

		/// <summary>
		/// The upper bound of LOD <paramref name="lod"/>'s range: 1 for LOD0, else the previous LOD's threshold.
		/// </summary>
		internal static float LodRangeTop(ReadOnlySpan<float> screenSizes, int lod)
			=> lod == 0 ? 1f : math.saturate(screenSizes[lod - 1]);

		/// <summary>
		/// The lower bound of LOD <paramref name="lod"/>'s range: its threshold, or 0 for the last LOD.
		/// </summary>
		internal static float LodRangeBottom(ReadOnlySpan<float> screenSizes, int lod)
			=> lod == screenSizes.Length - 1 ? 0f : math.saturate(screenSizes[lod]);

		/// <summary>
		/// Bar position (0 = left, 1 = right) of a screen size: square-root scale, 100% on the left.
		/// </summary>
		internal static float ScreenSizeToBar(float screenSize) => 1f - math.sqrt(math.saturate(screenSize));

		/// <summary>
		/// The screen size at a bar position (inverse of <see cref="ScreenSizeToBar"/>).
		/// </summary>
		internal static float BarToScreenSize(float bar)
		{
			var root = 1f - math.saturate(bar);
			return root * root;
		}

		private static Color[] Lightened(Color[] colors, float amount)
		{
			var result = new Color[colors.Length];
			for (var i = 0; i < colors.Length; i++)
				result[i] = Color.Lerp(colors[i], Color.white, amount);

			return result;
		}

		private static float[] Filled(int length, float value)
		{
			var result = new float[length];
			Array.Fill(result, value);
			return result;
		}

		/// <summary>
		/// Hands over the command a context menu queued, on a Layout event only: the caller's structural change is then
		/// laid out and painted consistently in the same frame. One command is pending at a time, for whichever bar lays
		/// out next.
		/// </summary>
		private static void TakePendingCommand(Event current, out LodBarCommand command, out int commandIndex)
		{
			command = LodBarCommand.None;
			commandIndex = -1;

			if (current.type != EventType.Layout || _pendingCommand == LodBarCommand.None)
				return;

			command = _pendingCommand;
			commandIndex = _pendingIndex;
			_pendingCommand = LodBarCommand.None;
		}

		/// <summary>
		/// Queues a command for the next Layout event (menu callbacks run outside the bar's OnGUI).
		/// </summary>
		private static void SetPending(LodBarCommand command, int lod)
		{
			_pendingCommand = command;
			_pendingIndex = lod;
		}

		private static float BarX(Rect position, float screenSize) => position.x + position.width * ScreenSizeToBar(screenSize);

		/// <summary>
		/// The screen size under <paramref name="x"/>.
		/// </summary>
		private static float ScreenSizeAt(Rect position, float x)
			=> BarToScreenSize((x - position.x) / math.max(1f, position.width));

		private static void DrawLodBar(Rect position, ReadOnlySpan<float> screenSizes, int selected)
		{
			EditorGUI.DrawRect(position, Background);

			for (var lod = 0; lod < screenSizes.Length; lod++)
			{
				var top = LodRangeTop(screenSizes, lod);
				var bottom = LodRangeBottom(screenSizes, lod);
				var left = BarX(position, top);
				var right = BarX(position, bottom);
				var segment = new Rect(left, position.y, math.max(0f, right - left), position.height);

				var isSelected = lod == selected;
				var colorIndex = lod % LodColors.Length;
				EditorGUI.DrawRect(segment, isSelected ? SelectedLodColors[colorIndex] : LodColors[colorIndex]);

				if (isSelected)
					DrawOutline(segment, SelectedOutline);

				var labelRect = new Rect(segment.x + 3f, segment.y + 2f, math.max(0f, segment.width - 4f), segment.height - 4f);
				GUI.Label(labelRect, LodLabel(lod, top, bottom), EditorStyles.whiteMiniLabel);
			}

			// Boundaries between segments.
			for (var lod = 0; lod < screenSizes.Length - 1; lod++)
			{
				var x = BarX(position, screenSizes[lod]);
				EditorGUI.DrawRect(new Rect(x - 1f, position.y, 2f, position.height), Background);
			}
		}

		/// <summary>
		/// Shows a resize cursor over each boundary (on Repaint).
		/// </summary>
		private static void AddBoundaryCursors(Rect position, ReadOnlySpan<float> screenSizes)
		{
			for (var lod = 0; lod < screenSizes.Length - 1; lod++)
			{
				var x = BarX(position, screenSizes[lod]);
				var grab = new Rect(x - BoundaryGrabWidth, position.y, 2f * BoundaryGrabWidth, position.height);
				EditorGUIUtility.AddCursorRect(grab, MouseCursor.ResizeHorizontal);
			}
		}

		private static void DrawOutline(Rect rect, Color color)
		{
			EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
			EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
			EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
			EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
		}

		/// <summary>
		/// "LOD n" and the percentage the LOD starts at, as LODGroup labels its segments (100% for LOD0); the tooltip gives
		/// the whole range. Cached per index.
		/// </summary>
		private static GUIContent LodLabel(int lod, float top, float bottom)
		{
			if (lod >= LabelCacheSize)
				return GUIContent.none;

			var label = LodLabels[lod] ??= new GUIContent();

			if (LodLabelTops[lod].Equals(top) && LodLabelBottoms[lod].Equals(bottom))
				return label;
			
			LodLabelTops[lod] = top;
			LodLabelBottoms[lod] = bottom;
			label.text = $"LOD {lod}\n{top * 100f:0.#}%";
			label.tooltip = $"LOD {lod}: lights from {top * 100f:0.##}% down to {bottom * 100f:0.##}% of the screen height";

			return label;
		}

		/// <summary>
		/// The boundary under <paramref name="x"/> (index of the LOD above it), or -1.
		/// </summary>
		private static int BoundaryAt(Rect position, ReadOnlySpan<float> screenSizes, float x)
		{
			var best = -1;
			var bestDistance = BoundaryGrabWidth;

			for (var lod = 0; lod < screenSizes.Length - 1; lod++)
			{
				var distance = math.abs(BarX(position, screenSizes[lod]) - x);
				if (distance <= bestDistance)
				{
					best = lod;
					bestDistance = distance;
				}
			}

			return best;
		}

		/// <summary>
		/// The LOD whose segment contains <paramref name="x"/>.
		/// </summary>
		private static int SegmentAt(Rect position, ReadOnlySpan<float> screenSizes, float x)
		{
			var size = ScreenSizeAt(position, x);
			for (var lod = 0; lod < screenSizes.Length - 1; lod++)
			{
				if (size >= screenSizes[lod])
					return lod;
			}

			return screenSizes.Length - 1;
		}

		/// <summary>
		/// Moves the boundary below LOD <paramref name="lod"/> to <paramref name="x"/>, kept between its neighbours.
		/// </summary>
		private static bool DragBoundary(Rect position, Span<float> screenSizes, int lod, float x)
		{
			var top = LodRangeTop(screenSizes, lod);
			var bottom = lod + 1 < screenSizes.Length - 1 ? math.saturate(screenSizes[lod + 1]) : 0f;
			var size = math.clamp(ScreenSizeAt(position, x), bottom, top);

			if (size.Equals(screenSizes[lod]))
				return false;

			screenSizes[lod] = size;
			return true;
		}

		private static void ShowLodMenu(int lod, int count, int maxCount)
		{
			var menu = new GenericMenu();

			if (count < maxCount)
				menu.AddItem(InsertBeforeLabel, false, () => SetPending(LodBarCommand.InsertBefore, lod));
			else
				menu.AddDisabledItem(InsertBeforeLabel);

			if (count > 1)
				menu.AddItem(DeleteLabel, false, () => SetPending(LodBarCommand.Delete, lod));
			else
				menu.AddDisabledItem(DeleteLabel);

			menu.ShowAsContext();
		}
	}
}
