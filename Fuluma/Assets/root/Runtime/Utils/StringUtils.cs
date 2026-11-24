namespace Saesentsessis.Fuluma.Utils
{
	/// <summary>
	/// String helpers for the package's log messages.
	/// </summary>
	internal static partial class StringUtils
	{
		private const string FulumaColoredPrefix = "[<color=#cc77cc>Fuluma</color>] "; 
		
		/// <summary>
		/// Prepends the package's colored <c>[Fuluma]</c> log prefix to <paramref name="other"/>.
		/// </summary>
		public static string PrependFulumaPrefix(string other)
			=> string.Concat(FulumaColoredPrefix, other);
	}
}