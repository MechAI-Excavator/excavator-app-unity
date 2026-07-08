using UnityEngine;

/// <summary>
/// Converts decoded CAN PWM channels into the camera motion state used by UILogic.
/// </summary>
public class CanMotionCameraSwitcher : MonoBehaviour
{
    [Header("References")]
    public SocketCanUnity socketCan;
    public UILogic uiLogic;

    [Header("PWM Channels")]
    [Min(0)] public int cabinChannel = 3;
    [Min(0)] public int leftTrackChannel = 4;
    [Min(0)] public int rightTrackChannel = 5;

    [Header("PWM Thresholds")]
    [Tooltip("PWM neutral value. Current CAN codec uses 0..1000, so 500 is the center.")]
    public int neutralPwm = 500;
    [Tooltip("Values within +/- deadZone around neutral are treated as stopped.")]
    [Min(0)] public int deadZone = 60;

    [Header("Direction Polarity")]
    [Tooltip("Default: cabin PWM above neutral means counter-clockwise/left turn.")]
    public bool invertCabinDirection;
    [Tooltip("Default: left track PWM above neutral means forward.")]
    public bool invertLeftTrackDirection;
    [Tooltip("Default: right track PWM above neutral means forward.")]
    public bool invertRightTrackDirection;

    [Header("Behavior")]
    [Tooltip("When cabin is rotating, side camera selection ignores track motion.")]
    public bool cabinHasPriority = true;
    [Tooltip("When CAN channels are neutral, return to the idle/front-main camera state.")]
    public bool publishIdleWhenNeutral = true;
    [Tooltip("Print PWM and resolved motion state for field debugging.")]
    public bool debugLog;

    bool _subscribed;
    VehicleMotionState _lastPublishedState = VehicleMotionState.Idle;
    bool _debugKeyboardActive;

    void Awake()
    {
        ResolveReferences();
    }

    void OnEnable()
    {
        ResolveReferences();
        Subscribe();
    }

    void Start()
    {
        ResolveReferences();
        Subscribe();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void Update()
    {
        if (debugLog)
            HandleDebugKeyboardInput();
    }

    void ResolveReferences()
    {
        if (socketCan == null)
            socketCan = FindObjectOfType<SocketCanUnity>();
        if (uiLogic == null)
            uiLogic = FindObjectOfType<UILogic>();
    }

    void Subscribe()
    {
        if (_subscribed || socketCan == null)
            return;

        socketCan.OnPwmFrame += OnPwmFrame;
        _subscribed = true;

        if (debugLog)
            Debug.Log("[CanMotionCameraSwitcher] Subscribed to SocketCanUnity.OnPwmFrame.");
    }

    void Unsubscribe()
    {
        if (!_subscribed || socketCan == null)
            return;

        socketCan.OnPwmFrame -= OnPwmFrame;
        _subscribed = false;
    }

    void OnPwmFrame(ushort[] pwm)
    {
        if (_debugKeyboardActive)
            return;

        if (uiLogic == null)
            ResolveReferences();
        if (uiLogic == null || pwm == null)
        {
            if (debugLog)
                Debug.LogWarning("[CanMotionCameraSwitcher] Missing UILogic or PWM frame.");
            return;
        }

        if (!TryResolveMotionState(pwm, out var state))
        {
            if (debugLog)
                Debug.LogWarning($"[CanMotionCameraSwitcher] Cannot resolve state from PWM length={pwm.Length}.");
            return;
        }

        if (debugLog && state != _lastPublishedState)
        {
            Debug.Log($"[CanMotionCameraSwitcher] PWM=[{string.Join(", ", pwm)}] -> {state}");
            _lastPublishedState = state;
        }

        uiLogic.SetVehicleMotionState(state);
    }

    [ContextMenu("Debug/Force Forward")]
    public void DebugForceForward()
    {
        ResolveReferences();
        PublishState(VehicleMotionState.Forward, "debug context menu");
    }

    void HandleDebugKeyboardInput()
    {
        bool forward = Input.GetKey(KeyCode.UpArrow);
        bool rear = Input.GetKey(KeyCode.DownArrow);
        bool left = Input.GetKey(KeyCode.LeftArrow);
        bool right = Input.GetKey(KeyCode.RightArrow);
        bool hasInput = forward || rear || left || right;

        if (!hasInput)
        {
            if (_debugKeyboardActive)
            {
                _debugKeyboardActive = false;
                PublishState(VehicleMotionState.Idle, "debug keyboard");
            }
            return;
        }

        _debugKeyboardActive = true;

        VehicleMotionState state;
        if (rear && left)
            state = VehicleMotionState.RearLeftTurn;
        else if (rear && right)
            state = VehicleMotionState.RearRightTurn;
        else if (rear)
            state = VehicleMotionState.RearTurn;
        else if (left)
            state = VehicleMotionState.LeftTurn;
        else if (right)
            state = VehicleMotionState.RightTurn;
        else if (forward)
            state = VehicleMotionState.Forward;
        else
            state = VehicleMotionState.Idle;

        PublishState(state, "debug keyboard");
    }

    void PublishState(VehicleMotionState state, string source)
    {
        if (uiLogic == null)
            ResolveReferences();
        if (uiLogic == null)
        {
            if (debugLog)
                Debug.LogWarning($"[CanMotionCameraSwitcher] Missing UILogic for {source}.");
            return;
        }

        if (debugLog && state != _lastPublishedState)
        {
            Debug.Log($"[CanMotionCameraSwitcher] {source} -> {state}");
            _lastPublishedState = state;
        }

        uiLogic.SetVehicleMotionState(state);
    }

    bool TryResolveMotionState(ushort[] pwm, out VehicleMotionState state)
    {
        if (cabinHasPriority &&
            TryGetSignedChannel(pwm, cabinChannel, invertCabinDirection, out int cabinDelta) &&
            Mathf.Abs(cabinDelta) > deadZone)
        {
            state = cabinDelta > 0
                ? VehicleMotionState.LeftTurn
                : VehicleMotionState.RightTurn;
            return true;
        }

        bool hasLeftTrack = TryGetSignedChannel(pwm, leftTrackChannel, invertLeftTrackDirection, out int leftTrackDelta);
        bool hasRightTrack = TryGetSignedChannel(pwm, rightTrackChannel, invertRightTrackDirection, out int rightTrackDelta);
        if (!hasLeftTrack || !hasRightTrack)
        {
            state = VehicleMotionState.Idle;
            return false;
        }

        int linear = (leftTrackDelta + rightTrackDelta) / 2;
        int yaw = (rightTrackDelta - leftTrackDelta) / 2;
        bool isRear = linear < -deadZone;
        bool isForward = linear > deadZone;
        bool isLeftTurn = yaw > deadZone;
        bool isRightTurn = yaw < -deadZone;

        if (isRear && isLeftTurn)
        {
            state = VehicleMotionState.RearLeftTurn;
            return true;
        }

        if (isRear && isRightTurn)
        {
            state = VehicleMotionState.RearRightTurn;
            return true;
        }

        if (isRear)
        {
            state = VehicleMotionState.RearTurn;
            return true;
        }

        if (isLeftTurn)
        {
            state = VehicleMotionState.LeftTurn;
            return true;
        }

        if (isRightTurn)
        {
            state = VehicleMotionState.RightTurn;
            return true;
        }

        if (isForward)
        {
            state = VehicleMotionState.Forward;
            return true;
        }

        state = VehicleMotionState.Idle;
        return publishIdleWhenNeutral;
    }

    bool TryGetSignedChannel(ushort[] pwm, int channel, bool invert, out int delta)
    {
        delta = 0;
        if (channel < 0 || channel >= pwm.Length)
            return false;

        delta = pwm[channel] - neutralPwm;
        if (invert)
            delta = -delta;
        return true;
    }
}
