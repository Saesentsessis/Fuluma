using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Utils
{
	/// <summary>
	/// Identity comparison. Needed for <c>UnityEngine.Object</c> keys: their equality treats every destroyed object as
	/// equal to null.
	/// </summary>
#if UNITY_6000_5_OR_NEWER
	// Immutable: a stateless shared instance.
	[NoAutoStaticsCleanup]
#endif
	internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
	{
		public static readonly ReferenceEqualityComparer<T> Instance = new();

		public bool Equals(T x, T y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
	}
}
