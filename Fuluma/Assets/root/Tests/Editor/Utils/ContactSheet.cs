using UnityEngine;

namespace Saesentsessis.Fuluma.Tests.Editor.Utils
{
	/// <summary>
	/// A grid of square tiles saved as one PNG, row 0 at the top.
	/// </summary>
	internal sealed class ContactSheet
	{
		private readonly int _tile;
		private readonly int _columns;
		private readonly int _rows;
		private readonly Color[] _pixels;

		public ContactSheet(int tile, int columns, int rows)
		{
			_tile = tile;
			_columns = columns;
			_rows = rows;
			_pixels = new Color[tile * columns * tile * rows];
		}

		private int Width => _tile * _columns;

		private int Height => _tile * _rows;

		/// <param name="tile">Bottom-left origin, like a texture readback.</param>
		public void Place(int column, int row, Color[] tile)
		{
			// Texture rows start at the bottom, so row 0 goes to the top band.
			var originX = column * _tile;
			var originY = (_rows - 1 - row) * _tile;

			for (var y = 0; y < _tile; y++)
			for (var x = 0; x < _tile; x++)
				_pixels[(originY + y) * Width + originX + x] = tile[y * _tile + x];
		}

		public void Save(string path) => TestImages.WritePng(path, _pixels, Width, Height);
	}
}
