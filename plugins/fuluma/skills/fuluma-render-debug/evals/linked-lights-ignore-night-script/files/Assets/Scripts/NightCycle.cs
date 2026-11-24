using UnityEngine;

public sealed class NightCycle : MonoBehaviour
{
	[SerializeField] private Light[] streetLamps;
	[SerializeField, Range(0f, 24f)] private float hour = 18f;
	[SerializeField] private float hoursPerSecond = 0.5f;

	private void Update()
	{
		hour = (hour + hoursPerSecond * Time.deltaTime) % 24f;
		var night = hour >= 19f || hour < 6f;
		foreach (var lamp in streetLamps)
			lamp.enabled = night;
	}
}
