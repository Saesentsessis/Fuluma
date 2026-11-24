using UnityEngine;

namespace Saesentsessis.Fuluma.Rendering.Proxy
{
	/// <summary>
	/// Every proxy LOD of a <see cref="SpotLightProxyArrayDescriptor"/>, built by <see cref="SpotLightProxyMeshBuilder"/>:
	/// one mesh with one submesh per LOD (finest first), each LOD's shape for the vertex shader, and the screen sizes
	/// culling switches LODs at.
	/// </summary>
	/// <remarks>The owner destroys <see cref="Mesh"/>.</remarks>
	internal readonly struct SpotLightProxyLods
	{
		/// <summary>
		/// The proxy mesh: one submesh per LOD.
		/// </summary>
		public readonly Mesh Mesh;

		/// <summary>
		/// Per LOD, how the vertex shader widens and inflates it around a light.
		/// </summary>
		public readonly SpotLightProxyShape[] Shapes;

		/// <summary>
		/// Smallest screen size (bounding-sphere diameter over screen height) drawn with each LOD, non-increasing.
		/// </summary>
		public readonly float[] ScreenSizes;

		public SpotLightProxyLods(Mesh mesh, SpotLightProxyShape[] shapes, float[] screenSizes)
		{
			Mesh = mesh;
			Shapes = shapes;
			ScreenSizes = screenSizes;
		}

		public int Count => Shapes?.Length ?? 0;
	}
}
