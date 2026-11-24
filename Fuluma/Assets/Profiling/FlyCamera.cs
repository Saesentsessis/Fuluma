using UnityEngine;

namespace Saesentsessis.Fuluma.Tests
{
	/// <summary>
	/// Free-flying camera for profiling (Editor play mode or a player build), through the legacy Input Manager:
	/// <list type="bullet">
	/// <item>W / S, D / A move along the camera's forward and right vectors (and their inverses); Left Shift is faster,
	/// the mouse wheel scales the speed.</item>
	/// <item>A key toggles mouse look (the cursor is locked while it's on; Escape also turns it off).</item>
	/// <item>Six keys turn the camera around its own axes: pitch, yaw and roll.</item>
	/// <item>Two keys reset the position (world origin, or where it started) and the rotation (the rotation it had in
	/// <c>Awake</c>, or none).</item>
	/// </list>
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class FlyCamera : MonoBehaviour
	{
		[Header("Movement")]
		[Tooltip("Units per second.")]
		[SerializeField, Min(0.01f)] private float moveSpeed = 10f;

		[Tooltip("Speed multiplier while Left Shift is held.")]
		[SerializeField, Min(1f)] private float fastMultiplier = 4f;

		[Tooltip("Each mouse wheel notch multiplies (up) or divides (down) the move speed by this.")]
		[SerializeField, Min(1f)] private float wheelSpeedStep = 1.25f;

		[Header("Rotation")]
		[SerializeField] private KeyCode mouseLookToggle = KeyCode.M;

		[Tooltip("Degrees per unit of mouse movement.")]
		[SerializeField, Min(0f)] private float mouseSensitivity = 2f;

		[Tooltip("Degrees per second while a rotation key is held.")]
		[SerializeField, Min(0f)] private float keyTurnSpeed = 60f;

		[SerializeField] private KeyCode pitchUp = KeyCode.I;
		[SerializeField] private KeyCode pitchDown = KeyCode.K;
		[SerializeField] private KeyCode yawLeft = KeyCode.J;
		[SerializeField] private KeyCode yawRight = KeyCode.L;
		[SerializeField] private KeyCode rollLeft = KeyCode.U;
		[SerializeField] private KeyCode rollRight = KeyCode.O;

		[Header("Reset")]
		[SerializeField] private KeyCode resetPosition = KeyCode.R;
		[SerializeField] private KeyCode resetRotation = KeyCode.T;

		[Tooltip("Off: the position resets to the world origin. On: to the position the camera had in Awake.")]
		[SerializeField] private bool resetPositionToStart;

		[Tooltip("On: the rotation resets to the rotation the camera had in Awake. Off: to no rotation (looking along +Z).")]
		[SerializeField] private bool resetRotationToStart = true;

		[Header("Help")]
		[SerializeField] private KeyCode helpToggle = KeyCode.F1;
		[SerializeField] private bool showHelp = true;

		private const float MinMoveSpeed = 0.01f;
		private const float MaxMoveSpeed = 10000f;

		private Vector3 _startPosition;
		private Quaternion _startRotation;
		private bool _mouseLook;

		private GUIStyle _helpStyle;
		private GUIContent _helpContent;
		private float _helpSpeed;
		private bool _helpMouseLook;

		private void Awake()
		{
			_startPosition = transform.position;
			_startRotation = transform.rotation;

			// Keyboard and mouse controls: the help would only cover the stats on a phone.
			if (Application.isMobilePlatform)
				showHelp = false;
		}

		private void OnDisable() => SetMouseLook(false);

		private void Update()
		{
			HandleToggles();

			var deltaTime = Time.unscaledDeltaTime;
			Rotate(deltaTime);
			Move(deltaTime);
			HandleResets();
		}

		private void HandleToggles()
		{
			if (Input.GetKeyDown(mouseLookToggle))
				SetMouseLook(_mouseLook == false);
			else if (_mouseLook && Input.GetKeyDown(KeyCode.Escape))
				SetMouseLook(false);

			if (Input.GetKeyDown(helpToggle))
				showHelp = showHelp == false;
		}

		/// <summary>
		/// Turns around the camera's own axes (keys and mouse alike), so roll and pitch stay consistent with each other.
		/// </summary>
		private void Rotate(float deltaTime)
		{
			var step = keyTurnSpeed * deltaTime;

			// Unity's rotations are left-handed: +X pitches down, +Y yaws right, +Z rolls left.
			var pitch = KeyAxis(pitchDown, pitchUp) * step;
			var yaw = KeyAxis(yawRight, yawLeft) * step;
			var roll = KeyAxis(rollLeft, rollRight) * step;

			if (_mouseLook)
			{
				yaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
				pitch -= Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
			}

			if (pitch != 0f || yaw != 0f || roll != 0f)
				transform.rotation *= Quaternion.Euler(pitch, yaw, roll);
		}

		private void Move(float deltaTime)
		{
			var wheel = Input.mouseScrollDelta.y;
			if (wheel != 0f)
				moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(wheelSpeedStep, wheel), MinMoveSpeed, MaxMoveSpeed);

			var direction = transform.forward * KeyAxis(KeyCode.W, KeyCode.S) + transform.right * KeyAxis(KeyCode.D, KeyCode.A);
			if (direction == Vector3.zero)
				return;

			var speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * fastMultiplier : moveSpeed;
			transform.position += direction.normalized * (speed * deltaTime);
		}

		private void HandleResets()
		{
			if (Input.GetKeyDown(resetPosition))
				transform.position = resetPositionToStart ? _startPosition : Vector3.zero;

			if (Input.GetKeyDown(resetRotation))
				transform.rotation = resetRotationToStart ? _startRotation : Quaternion.identity;
		}

		private void SetMouseLook(bool enabled)
		{
			_mouseLook = enabled;
			Cursor.lockState = enabled ? CursorLockMode.Locked : CursorLockMode.None;
			Cursor.visible = enabled == false;
		}

		/// <returns>+1 while only <paramref name="positive"/> is held, -1 while only <paramref name="negative"/> is.</returns>
		private static float KeyAxis(KeyCode positive, KeyCode negative)
			=> (Input.GetKey(positive) ? 1f : 0f) - (Input.GetKey(negative) ? 1f : 0f);

		private void OnGUI()
		{
			if (showHelp == false)
				return;

			// Scaled up on dense screens (phones): 1 up to 160 dpi.
			_helpStyle ??= new GUIStyle(GUI.skin.box)
			{
				alignment = TextAnchor.UpperLeft,
				fontSize = Mathf.RoundToInt(13 * Mathf.Max(1f, Screen.dpi / 160f)),
				richText = false
			};
			_helpContent ??= new GUIContent();

			// Rebuilt only when something it shows changes: no garbage per frame in a profiling build.
			if (string.IsNullOrEmpty(_helpContent.text) ||_helpSpeed != moveSpeed || _helpMouseLook != _mouseLook)
				UpdateHelpText();

			var size = _helpStyle.CalcSize(_helpContent);
			GUI.Box(new Rect(10f, Screen.height - size.y - 10f, size.x, size.y), _helpContent, _helpStyle);
		}

		private void UpdateHelpText()
		{
			_helpSpeed = moveSpeed;
			_helpMouseLook = _mouseLook;
			_helpContent.text =
				$"Move: W/S forward/back, D/A right/left, Shift faster, wheel speed ({moveSpeed:0.##}/s)\n" +
				$"Mouse look: {mouseLookToggle} ({(_mouseLook ? "on" : "off")}, Esc turns it off)\n" +
				$"Turn: {pitchUp}/{pitchDown} pitch, {yawLeft}/{yawRight} yaw, {rollLeft}/{rollRight} roll\n" +
				$"Reset: {resetPosition} position ({(resetPositionToStart ? "start" : "origin")}), " +
				$"{resetRotation} rotation ({(resetRotationToStart ? "start" : "none")})\n" +
				$"{helpToggle}: hide this help";
		}
	}
}
