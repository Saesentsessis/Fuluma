using System;
using Saesentsessis.Fuluma.Core;
using Saesentsessis.Fuluma.Data;
using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Components
{
	/// <summary>
	/// The clip boxes of a light component: exactly <see cref="Count"/> slots, each a box and the role it plays for the
	/// light. A slot without a box is unused; the used ones may sit anywhere among the four.
	/// </summary>
	/// <remarks>
	/// A value type: reading <see cref="FakeLightComponent.ClipBoxes"/> copies it without allocating. Change a copy and
	/// assign it back to apply it.
	/// </remarks>
	[Serializable]
	public struct ClipBoxSlots : IEquatable<ClipBoxSlots>
	{
		/// <summary>
		/// Number of slots: the most clip boxes a light can use (<see cref="LightStore.MaxClipBoxesPerLight"/>).
		/// </summary>
		public const int Count = 4;

		/// <summary>
		/// No clip boxes: every slot unused.
		/// </summary>
		public static ClipBoxSlots None => default;

		/// <summary>Whether both have the same box and role in every slot.</summary>
		public static bool operator ==(ClipBoxSlots left, ClipBoxSlots right) => left.Equals(right);

		/// <summary>Whether any slot differs in box or role.</summary>
		public static bool operator !=(ClipBoxSlots left, ClipBoxSlots right) => left.Equals(right) == false;

		// Boxes first, then roles: four references and four bytes pack into 40 B (one ClipBoxSlot per slot took 64 B).
		[SerializeField, Tooltip("First clip box of the light. Leave empty to leave the slot unused.")]
		private FakeLightClipBox box0;

		[SerializeField, Tooltip("Second clip box of the light. Leave empty to leave the slot unused.")]
		private FakeLightClipBox box1;

		[SerializeField, Tooltip("Third clip box of the light. Leave empty to leave the slot unused.")]
		private FakeLightClipBox box2;

		[SerializeField, Tooltip("Fourth clip box of the light. Leave empty to leave the slot unused.")]
		private FakeLightClipBox box3;

		[SerializeField, Tooltip("Role of the first clip box.")]
		private ClipBoxRole role0;

		[SerializeField, Tooltip("Role of the second clip box.")]
		private ClipBoxRole role1;

		[SerializeField, Tooltip("Role of the third clip box.")]
		private ClipBoxRole role2;

		[SerializeField, Tooltip("Role of the fourth clip box.")]
		private ClipBoxRole role3;

		/// <summary>
		/// Fills the slots in order; slots left out stay unused.
		/// </summary>
		/// <param name="slot0">First slot.</param>
		/// <param name="slot1">Second slot.</param>
		/// <param name="slot2">Third slot.</param>
		/// <param name="slot3">Fourth slot.</param>
		public ClipBoxSlots(ClipBoxSlot slot0, ClipBoxSlot slot1 = default, ClipBoxSlot slot2 = default, ClipBoxSlot slot3 = default)
		{
			box0 = slot0.Box;
			box1 = slot1.Box;
			box2 = slot2.Box;
			box3 = slot3.Box;
			role0 = slot0.Role;
			role1 = slot1.Role;
			role2 = slot2.Role;
			role3 = slot3.Role;
		}

		/// <summary>
		/// First slot.
		/// </summary>
		public ClipBoxSlot Slot0
		{
			readonly get => new(box0, role0);
			set => (box0, role0) = (value.Box, value.Role);
		}

		/// <summary>
		/// Second slot.
		/// </summary>
		public ClipBoxSlot Slot1
		{
			readonly get => new(box1, role1);
			set => (box1, role1) = (value.Box, value.Role);
		}

		/// <summary>
		/// Third slot.
		/// </summary>
		public ClipBoxSlot Slot2
		{
			readonly get => new(box2, role2);
			set => (box2, role2) = (value.Box, value.Role);
		}

		/// <summary>
		/// Fourth slot.
		/// </summary>
		public ClipBoxSlot Slot3
		{
			readonly get => new(box3, role3);
			set => (box3, role3) = (value.Box, value.Role);
		}

		/// <summary>
		/// The slot at <paramref name="index"/> (0 to <see cref="Count"/> - 1).
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 to <see cref="Count"/> - 1.
		/// </exception>
		public ClipBoxSlot this[int index]
		{
			readonly get => index switch
			{
				0 => Slot0,
				1 => Slot1,
				2 => Slot2,
				3 => Slot3,
				_ => throw new ArgumentOutOfRangeException(nameof(index), index, $"A light has {Count} clip box slots.")
			};
			set
			{
				switch (index)
				{
					case 0: Slot0 = value; break;
					case 1: Slot1 = value; break;
					case 2: Slot2 = value; break;
					case 3: Slot3 = value; break;
					default: throw new ArgumentOutOfRangeException(nameof(index), index, $"A light has {Count} clip box slots.");
				}
			}
		}

		/// <summary>
		/// Number of slots that have a box.
		/// </summary>
		public readonly int UsedCount
			=> (box0 != null ? 1 : 0) + (box1 != null ? 1 : 0) + (box2 != null ? 1 : 0) + (box3 != null ? 1 : 0);

		/// <summary>
		/// Whether every slot has the same box and role as the same slot of <paramref name="other"/>.
		/// </summary>
		public readonly bool Equals(ClipBoxSlots other)
			=> box0 == other.box0 && box1 == other.box1 && box2 == other.box2 && box3 == other.box3
			   && role0 == other.role0 && role1 == other.role1 && role2 == other.role2 && role3 == other.role3;

		/// <inheritdoc/>
		public override readonly bool Equals(object obj) => obj is ClipBoxSlots other && Equals(other);

		/// <inheritdoc/>
		public override readonly int GetHashCode()
			=> HashCode.Combine(box0, box1, box2, box3, (byte)role0 | (byte)role1 << 8 | (byte)role2 << 16 | (byte)role3 << 24);
	}
}
