using UnityEngine;

public static class CursorManager
{
	public static bool GetFlag(CursorFlags flag) => throw null;
}

public enum CursorFlags
{
	None = 0,
	Pause = 1,
	Map = 2,
	SelectionMenu = 4,
	Dialogue = 8,
	NotInGame = 0x10,
	Chat = 0x20,
	EditorWindow = 0x40,
	Loading = 0x80,
	EmptyScene = 0x100
}

public class Pilot : MonoBehaviour
{
	public Aircraft aircraft;
}

public class Unit : MonoBehaviour
{
	public Rigidbody rb { get; private set; }
	public float radarAlt;
	public float speed;
}

public class Aircraft : Unit
{
	public UnitPart cockpit;
}

public class UnitPart : MonoBehaviour
{
	public Rigidbody rb;
	public Transform xform { get; private set; }
}

// Game has `forwardFlightController` as `protected` field and ForwardFlightController as a
// `protected` nested class inside Autopilot. Stub declares public so the compiler is happy;
// IgnoresAccessChecksTo("Assembly-CSharp") makes runtime skip the access check. Type identity
// at runtime requires the nested-class layout to match the game exactly.
public class Autopilot : MonoBehaviour
{
	public ForwardFlightController forwardFlightController;

	public class ForwardFlightController
	{
		public bool Enabled;
		public float referenceAirspeed;
		public PIDFactors pitchFlightPID;
		public PIDFactors yawFlightPID;
		public PIDFactors rollFlightPID;
		public void ApplyInputs(ControlInputs inputs, float airspeed, Vector3 error) => throw null;
		public void ApplyInputs(ControlInputs inputs, float airspeed, Vector3 error, float opacity) => throw null;
	}
}

public class PIDFactors
{
	public float P { get; }
	public float I { get; }
	public float D { get; }
}

public static class TargetCalc
{
	public static float GetAngleOnAxis(Vector3 self, Vector3 other, Vector3 axis) => throw null;
}

public static class GameManager
{
	public static Rewired.Player playerInput;
	public static bool flightControlsEnabled;
}

public class DynamicMap : SceneSingleton<DynamicMap>
{
	public static bool mapMaximized { get; }
}

public enum CameraMode
{
	cockpit,
	orbit,
	chase,
	tv,
	free,
	selection,
	relative,
	encyclopedia
}

public class CameraBaseState { }
public class CameraOrbitState : CameraBaseState
{
	public void UpdateState(CameraStateManager cam) => throw null;
}

public class CameraStateManager : SceneSingleton<CameraStateManager>
{
	public static CameraMode cameraMode;
	public Rigidbody followingRB;
	public Transform cameraPivot;
}

public abstract class PilotBaseState
{
	public Pilot pilot;
	public ControlInputs controlInputs;
}

public class PilotPlayerState : PilotBaseState
{
	public void PlayerAxisControls() => throw null;
}

public class ControlInputs
{
	public float pitch;
	public float yaw;
	public float roll;
}

public class SceneSingleton<T> : MonoBehaviour where T : class { public static T i; }
