using System;
using Unity.Mathematics;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif

namespace Saesentsessis.Fuluma.Utils
{
#if UNITY_6000_5_OR_NEWER
	// Immutable: unit labels.
	[NoAutoStaticsCleanup]
#endif
	/// <summary>
	/// Human-readable byte sizes in binary units (1 KB = 1024 B).
	/// </summary>
	internal static class FormatUtils
	{
		private static readonly string[] SizeLabels = 
		{
			"B",
			"KB",
			"MB",
			"GB",
			"TB",
			"PB",
			"EB"
		};

		/// <summary>
		/// Writes a byte size such as <c>512 B</c> or <c>1.50 MB</c> (two fraction digits, truncated, above bytes) into
		/// <paramref name="span"/> without allocating.
		/// </summary>
		/// <returns>False, with nothing written, when <paramref name="byteSize"/> is negative or the span is too short.
		/// </returns>
		public static bool TryFormatByteSize(Span<char> span, long byteSize, out int charsWritten)
		{
			charsWritten = 0;
			
			if (byteSize < 0)
				return false;
			
			var labelIndex = (63 - math.lzcnt((ulong)(byteSize | 1))) / 10;
			var shift = labelIndex * 10;
			var whole = (int)(byteSize >> shift); // always < 1024
			
			var digits = whole >= 1000 ? 4 : whole >= 100 ? 3 : whole >= 10 ? 2 : 1;
			var label = SizeLabels[labelIndex];

			// Digits, a space and the label; above bytes also a dot and two fraction digits.
			var charCount = digits + 1 + label.Length + (labelIndex > 0 ? 3 : 0);

			if (charCount > span.Length)
				return false;

			var fraction = -1;
			if (labelIndex > 0)
			{
				// Two fraction digits, truncated. Keep only the top 20 bits of the remainder so that
				// (remainder * 100) can never overflow.
				var remainder = byteSize & ((1L << shift) - 1);
				var preShift = shift > 20 ? shift - 20 : 0;
				fraction = (int)(((remainder >> preShift) * 100) >> (shift - preShift));
			}

			var value = whole;
			for (var i = digits - 1; i >= 0; i--)
			{
				span[i] = (char)('0' + value % 10);
				value /= 10;
			}

			var pos = digits;

			if (fraction >= 0)
			{
				span[pos++] = '.';
				span[pos++] = (char)('0' + fraction / 10);
				span[pos++] = (char)('0' + fraction % 10);
			}

			span[pos++] = ' ';
			for (var i = 0; i < label.Length; i++)
				span[pos + i] = label[i];
			
			charsWritten = charCount;
			return true;
		}
		
		/// <summary>
		/// Formats a byte size such as <c>512 B</c> or <c>1.50 MB</c> (two fraction digits, truncated, above bytes).
		/// </summary>
		/// <returns>The text, or <c>#ERROR(size &lt; 0)</c> for a negative size.</returns>
		public static string FormatByteSize(long byteSize)
		{
			if (byteSize < 0)
				return "#ERROR(size < 0)";

			var labelIndex = (63 - math.lzcnt((ulong)(byteSize | 1))) / 10;
			var shift = labelIndex * 10;
			var whole = (int)(byteSize >> shift); // always < 1024
			
			var digits = whole >= 1000 ? 4 : whole >= 100 ? 3 : whole >= 10 ? 2 : 1;
			var label = SizeLabels[labelIndex];
			var charCount = digits + 1 + label.Length;

			var fraction = -1;
			if (labelIndex > 0)
			{
				// Two fraction digits, truncated. Keep only the top 20 bits of the remainder so that
				// (remainder * 100) can never overflow.
				var remainder = byteSize & ((1L << shift) - 1);
				var preShift = shift > 20 ? shift - 20 : 0;
				fraction = (int)(((remainder >> preShift) * 100) >> (shift - preShift));
				charCount += 3; // a dot ('.') + two digits
			}
			
			return string.Create(charCount, (label, whole, fraction, digits), static (span, state) =>
			{
				var value = state.whole;
				for (var i = state.digits - 1; i >= 0; i--)
				{
					span[i] = (char)('0' + value % 10);
					value /= 10;
				}

				var pos = state.digits;

				if (state.fraction >= 0)
				{
					span[pos++] = '.';
					span[pos++] = (char)('0' + state.fraction / 10);
					span[pos++] = (char)('0' + state.fraction % 10);
				}

				span[pos++] = ' ';
				for (var i = 0; i < state.label.Length; i++)
					span[pos + i] = state.label[i];
			});
		}
	}
}