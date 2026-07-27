using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 车辆运动状态。后续接入 CAN/MQTT 时，只需要调用 SetVehicleMotionState。
/// </summary>
public enum VehicleMotionState
{
    Idle = 0,
    LeftTurn = 1,
    RightTurn = 2,
    RearTurn = 3,
    Forward = 4,
    RearLeftTurn = 5,
    RearRightTurn = 6
}

/// <summary>
/// 顶部遥测栏中可独立更新的字段。
/// </summary>
public enum TelemetryField
{
    SensorRtk,
    SensorImu,
    SensorLidar,
    SensorCamera,
    SensorRadar,
    BaseSignal,
    BasePing,
    BaseThroughput,
    BasePacketLoss,
    BaseRssi,
    NetworkRssi,
    NetworkOperator,
    NetworkSim,
    ComputeCpu,
    ComputeGpu,
    ComputeMemory,
    ComputeBoardTemperature,
    ComputeFan,
    ComputePower,
    ComputeVoltage,
    ComputeCurrent,
    PowerCabinetTemperature,
    PowerExternalFan,
    PowerBattery,
    PowerDevicePower
}

/// <summary>
/// 遥测值的显示级别，用于控制文字颜色。
/// </summary>
public enum TelemetryLevel
{
    Neutral,
    Good,
    Warning,
    Error
}

/// <summary>
/// 一条遥测 UI 更新。Text 应包含显示单位，例如 "42ms" 或 "67°C"。
/// </summary>
public readonly struct TelemetryUpdate
{
    public TelemetryField Field { get; }
    public string Text { get; }
    public TelemetryLevel Level { get; }

    public TelemetryUpdate(
        TelemetryField field,
        string text,
        TelemetryLevel level = TelemetryLevel.Neutral)
    {
        Field = field;
        Text = text;
        Level = level;
    }
}

/// <summary>
/// UIDocument 的车辆状态与遥测数据事件入口。
/// 当前提供 ContextMenu 和可选键盘 mock，实际业务事件可复用公开方法。
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class UILogic : MonoBehaviour
{
    [Header("开发调试")]
    [SerializeField] bool enableKeyboardMock;

    [SerializeField, Range(0f, 359f)]
    [Tooltip("运行时调试方位；仅在 Enable Keyboard Mock 开启时响应 Inspector 修改。")]
    float mockDirectionHeading;

    [Header("方位指示")]
    [SerializeField, Min(0f)]
    [Tooltip("方位 UI 的平滑跟随速度。设为 0 时立即指向最新角度。")]
    float directionIndicatorLerpSpeed = 8f;

    public VehicleMotionState CurrentState { get; private set; } = VehicleMotionState.Idle;

    public event Action<VehicleMotionState> VehicleMotionStateChanged;

    /// <summary>
    /// 遥测值完成 UI 刷新后在 Unity 主线程触发。
    /// </summary>
    public event Action<TelemetryUpdate> TelemetryValueChanged;

    readonly ConcurrentQueue<TelemetryUpdate> _pendingTelemetryUpdates =
        new ConcurrentQueue<TelemetryUpdate>();

    readonly Dictionary<TelemetryField, Label> _telemetryLabels =
        new Dictionary<TelemetryField, Label>();

    Button _leftButton;
    Button _forwardButton;
    Button _idleButton;
    Button _rightButton;
    Button _rearButton;
    Label _stateLabel;
    Label _boomAngleLabel;
    Label _stickAngleLabel;
    Label _bucketAngleLabel;
    VisualElement _navigationArrow;
    VisualElement _indicatorForwardRotator;
    bool _hasDisplayedJointAngles;
    ExcavatorJointAngles _displayedJointAngles;
    bool _loggedFirstJointAngleUpdate;
    bool _loggedDirectionIndicatorBinding;
    bool _directionHeadingReady;
    float _targetDirectionHeading;
    float _displayedDirectionHeading;
    VisualElement _settingsButton;
    VisualElement _settingsScrim;
    VisualElement _settingsDrawer;
    AndroidWebViewOverlay _webViewOverlay;
    readonly List<VisualElement> _settingsMenuItems = new List<VisualElement>();

    void OnEnable()
    {
        BindTestControls();
        BindTelemetryLabels();
        BindJointAngleLabels();
        BindDirectionIndicators();
        BindSettingsDrawer();
        UpdateStateLabel();

        ExcavatorJointStateStore.Changed += OnJointAnglesChanged;
        if (ExcavatorJointStateStore.TryGetLatest(out var angles))
            OnJointAnglesChanged(angles);
    }

    void OnDisable()
    {
        ExcavatorJointStateStore.Changed -= OnJointAnglesChanged;
        UnbindSettingsDrawer();
        UnbindTestControls();
        _telemetryLabels.Clear();
        _boomAngleLabel = null;
        _stickAngleLabel = null;
        _bucketAngleLabel = null;
        _navigationArrow = null;
        _indicatorForwardRotator = null;
        _hasDisplayedJointAngles = false;
        _directionHeadingReady = false;
    }

    void Update()
    {
        EnsureJointAngleLabels();
        EnsureDirectionIndicators();
        SyncLatestJointAngles();
        if (enableKeyboardMock)
            SetDirectionHeading(mockDirectionHeading);
        UpdateDirectionIndicators();
        FlushTelemetryUpdates();

        if (!enableKeyboardMock)
            return;

        if (Input.GetKeyDown(KeyCode.Alpha0))
            MockIdle();
        else if (Input.GetKeyDown(KeyCode.Alpha1))
            MockLeftTurn();
        else if (Input.GetKeyDown(KeyCode.Alpha2))
            MockRightTurn();
        else if (Input.GetKeyDown(KeyCode.Alpha3))
            MockRearTurn();
        else if (Input.GetKeyDown(KeyCode.Alpha4))
            MockForward();
        else if (Input.GetKeyDown(KeyCode.Alpha5))
            SetMockDirectionHeading(0f);
        else if (Input.GetKeyDown(KeyCode.Alpha6))
            SetMockDirectionHeading(90f);
        else if (Input.GetKeyDown(KeyCode.Alpha7))
            SetMockDirectionHeading(180f);
        else if (Input.GetKeyDown(KeyCode.Alpha8))
            SetMockDirectionHeading(270f);
        else if (Input.GetKeyDown(KeyCode.Alpha9))
            SetMockDirectionHeading(359f);
    }

    /// <summary>
    /// 正式事件接入点。重复状态不会产生多余 UI 刷新。
    /// </summary>
    public void SetVehicleMotionState(VehicleMotionState state)
    {
        if (CurrentState == state)
        {
            UpdateStateLabel();
            return;
        }

        CurrentState = state;
        UpdateStateLabel();
        VehicleMotionStateChanged?.Invoke(state);
    }

    /// <summary>
    /// 正式遥测数据入口，可从 MQTT/WebSocket 等后台回调线程调用。
    /// 数据会排队，并在下一帧由 Unity 主线程更新 UI。
    /// </summary>
    public void PublishTelemetry(
        TelemetryField field,
        string text,
        TelemetryLevel level = TelemetryLevel.Neutral)
    {
        _pendingTelemetryUpdates.Enqueue(new TelemetryUpdate(field, text, level));
    }

    /// <summary>
    /// 批量发布同一批次收到的遥测数据。
    /// </summary>
    public void PublishTelemetry(params TelemetryUpdate[] updates)
    {
        if (updates == null)
            return;

        foreach (var update in updates)
            _pendingTelemetryUpdates.Enqueue(update);
    }

    [ContextMenu("Mock/静止")]
    public void MockIdle()
    {
        SetVehicleMotionState(VehicleMotionState.Idle);
    }

    [ContextMenu("Mock/左转")]
    public void MockLeftTurn()
    {
        SetVehicleMotionState(VehicleMotionState.LeftTurn);
    }

    [ContextMenu("Mock/前进")]
    public void MockForward()
    {
        SetVehicleMotionState(VehicleMotionState.Forward);
    }

    [ContextMenu("Mock/右转")]
    public void MockRightTurn()
    {
        SetVehicleMotionState(VehicleMotionState.RightTurn);
    }

    [ContextMenu("Mock/后退")]
    public void MockRearTurn()
    {
        SetVehicleMotionState(VehicleMotionState.RearTurn);
    }

    [ContextMenu("方位 Mock/北 0°")]
    void MockHeadingNorth()
    {
        SetMockDirectionHeading(0f);
    }

    [ContextMenu("方位 Mock/东 90°")]
    void MockHeadingEast()
    {
        SetMockDirectionHeading(90f);
    }

    [ContextMenu("方位 Mock/南 180°")]
    void MockHeadingSouth()
    {
        SetMockDirectionHeading(180f);
    }

    [ContextMenu("方位 Mock/西 270°")]
    void MockHeadingWest()
    {
        SetMockDirectionHeading(270f);
    }

    [ContextMenu("方位 Mock/跨北 359°")]
    void MockHeadingNorthWrap()
    {
        SetMockDirectionHeading(359f);
    }

    void BindTestControls()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
            return;

        var root = document.rootVisualElement;
        _leftButton = root.Q<Button>("btn-mock-left");
        _forwardButton = root.Q<Button>("btn-mock-forward");
        _idleButton = root.Q<Button>("btn-mock-idle");
        _rightButton = root.Q<Button>("btn-mock-right");
        _rearButton = root.Q<Button>("btn-mock-rear");
        _stateLabel = root.Q<Label>("motion-state-label");

        if (_leftButton != null)
            _leftButton.clicked += MockLeftTurn;
        if (_forwardButton != null)
            _forwardButton.clicked += MockForward;
        if (_idleButton != null)
            _idleButton.clicked += MockIdle;
        if (_rightButton != null)
            _rightButton.clicked += MockRightTurn;
        if (_rearButton != null)
            _rearButton.clicked += MockRearTurn;
    }

    void BindJointAngleLabels()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
            return;

        var root = document.rootVisualElement;
        _boomAngleLabel = root.Q<Label>("boom-angle-value");
        _stickAngleLabel = root.Q<Label>("stick-angle-value");
        _bucketAngleLabel = root.Q<Label>("bucket-angle-value");
    }

    void BindDirectionIndicators()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
            return;

        var root = document.rootVisualElement;
        _navigationArrow = root.Q<VisualElement>(className: "navigation-arrow");
        _indicatorForwardRotator = root.Q<VisualElement>("indicator-forward-rotator");
    }

    void EnsureJointAngleLabels()
    {
        if (_boomAngleLabel?.panel != null
            && _stickAngleLabel?.panel != null
            && _bucketAngleLabel?.panel != null)
        {
            return;
        }

        BindJointAngleLabels();
        if (ExcavatorJointStateStore.TryGetLatest(out var angles))
            OnJointAnglesChanged(angles);
    }

    void EnsureDirectionIndicators()
    {
        if (_navigationArrow?.panel != null
            && _indicatorForwardRotator?.panel != null)
        {
            return;
        }

        BindDirectionIndicators();
        if (_directionHeadingReady)
            ApplyDirectionIndicatorRotation(_displayedDirectionHeading);
    }

    void OnJointAnglesChanged(ExcavatorJointAngles angles)
    {
        SetAngleText(_boomAngleLabel, angles.Boom);
        SetAngleText(_stickAngleLabel, angles.Stick);
        SetAngleText(_bucketAngleLabel, angles.Bucket);
        if (angles.HasRotate)
            SetDirectionHeading(angles.Rotate);
        _displayedJointAngles = angles;
        _hasDisplayedJointAngles = true;

        if (!_loggedFirstJointAngleUpdate)
        {
            _loggedFirstJointAngleUpdate = true;
            Debug.Log(
                $"[UI] 已更新首帧关节角度: boom={angles.Boom:F3} " +
                $"stick={angles.Stick:F3} bucket={angles.Bucket:F3}; " +
                $"labelsBound={_boomAngleLabel?.panel != null && _stickAngleLabel?.panel != null && _bucketAngleLabel?.panel != null}");
        }
    }

    void SyncLatestJointAngles()
    {
        if (!ExcavatorJointStateStore.TryGetLatest(out var angles))
            return;

        if (_hasDisplayedJointAngles
            && angles.Timestamp.Equals(_displayedJointAngles.Timestamp)
            && Mathf.Approximately(angles.Boom, _displayedJointAngles.Boom)
            && Mathf.Approximately(angles.Stick, _displayedJointAngles.Stick)
            && Mathf.Approximately(angles.Bucket, _displayedJointAngles.Bucket)
            && angles.HasRotate == _displayedJointAngles.HasRotate
            && (!angles.HasRotate
                || Mathf.Approximately(angles.Rotate, _displayedJointAngles.Rotate)))
        {
            return;
        }

        OnJointAnglesChanged(angles);
    }

    /// <summary>
    /// Updates both direction visuals from the absolute map heading:
    /// 0°=north/up, 90°=east/right, clockwise positive.
    /// </summary>
    public void SetDirectionHeading(float headingDegrees)
    {
        if (float.IsNaN(headingDegrees) || float.IsInfinity(headingDegrees))
            return;

        _targetDirectionHeading = Mathf.Repeat(headingDegrees, 360f);
        if (!_loggedDirectionIndicatorBinding)
        {
            _loggedDirectionIndicatorBinding = true;
            Debug.Log(
                $"[UI] 方位指示绑定: navigationArrow={_navigationArrow != null}, " +
                $"indicatorForwardRotator={_indicatorForwardRotator != null}");
        }

        if (_directionHeadingReady)
            return;

        _displayedDirectionHeading = _targetDirectionHeading;
        _directionHeadingReady = true;
        ApplyDirectionIndicatorRotation(_displayedDirectionHeading);
    }

    void SetMockDirectionHeading(float headingDegrees)
    {
        mockDirectionHeading = Mathf.Repeat(headingDegrees, 360f);
        SetDirectionHeading(mockDirectionHeading);
    }

    void UpdateDirectionIndicators()
    {
        if (!_directionHeadingReady)
            return;

        if (directionIndicatorLerpSpeed <= 0f)
        {
            _displayedDirectionHeading = _targetDirectionHeading;
        }
        else
        {
            float interpolation = 1f
                - Mathf.Exp(-directionIndicatorLerpSpeed * Time.unscaledDeltaTime);
            _displayedDirectionHeading = Mathf.LerpAngle(
                _displayedDirectionHeading,
                _targetDirectionHeading,
                interpolation);
        }

        ApplyDirectionIndicatorRotation(_displayedDirectionHeading);
    }

    void ApplyDirectionIndicatorRotation(float headingDegrees)
    {
        var rotation = new Rotate(new Angle(headingDegrees, AngleUnit.Degree));
        if (_navigationArrow != null)
            _navigationArrow.style.rotate = rotation;
        if (_indicatorForwardRotator != null)
            _indicatorForwardRotator.style.rotate = rotation;
    }

    static void SetAngleText(Label label, float angle)
    {
        if (label == null)
            return;

        label.text =
            angle.ToString("0.00", CultureInfo.InvariantCulture) + "°";
    }

    void UnbindTestControls()
    {
        if (_leftButton != null)
            _leftButton.clicked -= MockLeftTurn;
        if (_forwardButton != null)
            _forwardButton.clicked -= MockForward;
        if (_idleButton != null)
            _idleButton.clicked -= MockIdle;
        if (_rightButton != null)
            _rightButton.clicked -= MockRightTurn;
        if (_rearButton != null)
            _rearButton.clicked -= MockRearTurn;

        _leftButton = null;
        _forwardButton = null;
        _idleButton = null;
        _rightButton = null;
        _rearButton = null;
        _stateLabel = null;
    }

    void BindSettingsDrawer()
    {
        var document = GetComponent<UIDocument>();
        if (document == null)
            return;

        var root = document.rootVisualElement;
        _settingsButton = root.Q<VisualElement>("settings-button");
        _settingsScrim = root.Q<VisualElement>("settings-scrim");
        _settingsDrawer = root.Q<VisualElement>("settings-drawer");
        _webViewOverlay = GetComponent<AndroidWebViewOverlay>();

        if (_settingsButton != null)
            _settingsButton.RegisterCallback<ClickEvent>(OnSettingsButtonClicked);
        if (_settingsScrim != null)
            _settingsScrim.RegisterCallback<ClickEvent>(OnSettingsScrimClicked);
        if (_settingsDrawer != null)
            _settingsDrawer.RegisterCallback<ClickEvent>(OnSettingsDrawerBackgroundClicked);

        _settingsMenuItems.Clear();
        string[] itemNames =
        {
            "settings-menu-display",
            "settings-menu-control",
            "settings-menu-device",
            "settings-menu-safety",
            "settings-menu-data",
            "settings-menu-general"
        };

        foreach (string itemName in itemNames)
        {
            var item = root.Q<VisualElement>(itemName);
            if (item == null)
                continue;

            item.RegisterCallback<ClickEvent>(OnSettingsMenuItemClicked);
            _settingsMenuItems.Add(item);
        }
    }

    void UnbindSettingsDrawer()
    {
        if (_settingsButton != null)
            _settingsButton.UnregisterCallback<ClickEvent>(OnSettingsButtonClicked);
        if (_settingsScrim != null)
            _settingsScrim.UnregisterCallback<ClickEvent>(OnSettingsScrimClicked);
        if (_settingsDrawer != null)
            _settingsDrawer.UnregisterCallback<ClickEvent>(OnSettingsDrawerBackgroundClicked);

        foreach (var item in _settingsMenuItems)
        {
            if (item != null)
                item.UnregisterCallback<ClickEvent>(OnSettingsMenuItemClicked);
        }

        _settingsMenuItems.Clear();
        _settingsButton = null;
        _settingsScrim = null;
        _settingsDrawer = null;
        _webViewOverlay = null;
    }

    void OnSettingsButtonClicked(ClickEvent evt)
    {
        evt.StopPropagation();
        ShowSettingsDrawer();
    }

    void OnSettingsScrimClicked(ClickEvent evt)
    {
        evt.StopPropagation();
        HideSettingsDrawer();
    }

    void OnSettingsDrawerBackgroundClicked(ClickEvent evt)
    {
        evt.StopPropagation();
        HideSettingsDrawer();
    }

    void OnSettingsMenuItemClicked(ClickEvent evt)
    {
        evt.StopPropagation();
    }

    void ShowSettingsDrawer()
    {
        _webViewOverlay?.SetGlobalVisibility(false);
        _settingsScrim?.RemoveFromClassList("settings-hidden");
        _settingsDrawer?.RemoveFromClassList("settings-hidden");
    }

    void HideSettingsDrawer()
    {
        _settingsScrim?.AddToClassList("settings-hidden");
        _settingsDrawer?.AddToClassList("settings-hidden");
        _webViewOverlay?.SetGlobalVisibility(true);
    }

    void BindTelemetryLabels()
    {
        _telemetryLabels.Clear();

        var document = GetComponent<UIDocument>();
        if (document == null)
            return;

        var root = document.rootVisualElement;
        foreach (TelemetryField field in Enum.GetValues(typeof(TelemetryField)))
        {
            string elementName = GetTelemetryElementName(field);
            var label = root.Q<Label>(elementName);
            if (label == null)
            {
                Debug.LogWarning($"[UI] UXML 中找不到遥测标签: {elementName}");
                continue;
            }

            _telemetryLabels[field] = label;
        }
    }

    void FlushTelemetryUpdates()
    {
        while (_pendingTelemetryUpdates.TryDequeue(out var update))
        {
            if (_telemetryLabels.TryGetValue(update.Field, out var label))
            {
                label.text = string.IsNullOrEmpty(update.Text) ? "--" : update.Text;
                ApplyTelemetryLevel(label, update.Level);
            }

            TelemetryValueChanged?.Invoke(update);
        }
    }

    static void ApplyTelemetryLevel(Label label, TelemetryLevel level)
    {
        label.RemoveFromClassList("telemetry-value");
        label.RemoveFromClassList("telemetry-ok");
        label.RemoveFromClassList("telemetry-warning");
        label.RemoveFromClassList("telemetry-error");

        switch (level)
        {
            case TelemetryLevel.Good:
                label.AddToClassList("telemetry-ok");
                break;
            case TelemetryLevel.Warning:
                label.AddToClassList("telemetry-warning");
                break;
            case TelemetryLevel.Error:
                label.AddToClassList("telemetry-error");
                break;
            default:
                label.AddToClassList("telemetry-value");
                break;
        }
    }

    static string GetTelemetryElementName(TelemetryField field)
    {
        return field switch
        {
            TelemetryField.SensorRtk => "telemetry-sensor-rtk",
            TelemetryField.SensorImu => "telemetry-sensor-imu",
            TelemetryField.SensorLidar => "telemetry-sensor-lidar",
            TelemetryField.SensorCamera => "telemetry-sensor-camera",
            TelemetryField.SensorRadar => "telemetry-sensor-radar",
            TelemetryField.BaseSignal => "telemetry-base-signal",
            TelemetryField.BasePing => "telemetry-base-ping",
            TelemetryField.BaseThroughput => "telemetry-base-throughput",
            TelemetryField.BasePacketLoss => "telemetry-base-packet-loss",
            TelemetryField.BaseRssi => "telemetry-base-rssi",
            TelemetryField.NetworkRssi => "telemetry-network-rssi",
            TelemetryField.NetworkOperator => "telemetry-network-operator",
            TelemetryField.NetworkSim => "telemetry-network-sim",
            TelemetryField.ComputeCpu => "telemetry-compute-cpu",
            TelemetryField.ComputeGpu => "telemetry-compute-gpu",
            TelemetryField.ComputeMemory => "telemetry-compute-memory",
            TelemetryField.ComputeBoardTemperature => "telemetry-compute-board-temperature",
            TelemetryField.ComputeFan => "telemetry-compute-fan",
            TelemetryField.ComputePower => "telemetry-compute-power",
            TelemetryField.ComputeVoltage => "telemetry-compute-voltage",
            TelemetryField.ComputeCurrent => "telemetry-compute-current",
            TelemetryField.PowerCabinetTemperature => "telemetry-power-cabinet-temperature",
            TelemetryField.PowerExternalFan => "telemetry-power-external-fan",
            TelemetryField.PowerBattery => "telemetry-power-battery",
            TelemetryField.PowerDevicePower => "telemetry-power-device-power",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    void UpdateStateLabel()
    {
        if (_stateLabel == null)
            return;

        _stateLabel.text = CurrentState switch
        {
            VehicleMotionState.LeftTurn => "当前：左转",
            VehicleMotionState.Forward => "当前：前进",
            VehicleMotionState.RightTurn => "当前：右转",
            VehicleMotionState.RearTurn => "当前：后退",
            VehicleMotionState.RearLeftTurn => "当前：后退左转",
            VehicleMotionState.RearRightTurn => "当前：后退右转",
            _ => "当前：静止"
        };
    }
}
