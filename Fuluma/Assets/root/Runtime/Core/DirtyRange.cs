namespace Saesentsessis.Fuluma.Core
{
	/// <summary>
	/// Smallest contiguous interval covering every index marked since the last <see cref="Consume"/>.
	/// </summary>
	/// <remarks>
	/// One interval, not a list: two changes far apart upload everything between them. That's cheap for
	/// rare edits and keeps the bookkeeping free. <c>default</c> is empty.
	/// </remarks>
	internal struct DirtyRange
	{
		private int _start;
		private int _end; // exclusive

		public readonly bool IsEmpty => _end <= _start;

		public void Mark(int index)
		{
			if (IsEmpty)
			{
				_start = index;
				_end = index + 1;
				return;
			}

			if (index < _start)
				_start = index;

			if (index + 1 > _end)
				_end = index + 1;
		}

		/// <summary>
		/// Returns the marked interval clipped to <paramref name="length"/> and resets it.
		/// </summary>
		/// <param name="length">Current element count; indices past it were removed and need no upload.</param>
		public BufferRange Consume(int length)
		{
			var end = _end < length ? _end : length;
			var result = end > _start ? new BufferRange(_start, end - _start) : BufferRange.Invalid;

			this = default;
			return result;
		}

		public void Reset() => this = default;
	}
}
