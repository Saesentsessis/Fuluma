namespace Saesentsessis.Fuluma.Core.Lifecycle
{
	/// <summary>
	/// Something that keeps lights registered in a <see cref="LightStore"/> and can register them again
	/// after the stores were released.
	/// </summary>
	internal interface ILightStoreClient
	{
		/// <summary>
		/// Re-registers if the current handle is dead. Must be a no-op when the registration is still alive.
		/// </summary>
		void RestoreRegistration();
	}
}
