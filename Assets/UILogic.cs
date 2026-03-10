using System;
using UnityEngine;
using UnityEngine.UIElements;

public class UILogic : MonoBehaviour
{
    // ── UI 元素缓存 ──────────────────────────────────────────
    private VisualElement _dotConn;
    private Label _lblConnStatus;

    private ProgressBar _pbBattery;
    private Label _lblBatteryPct;
    private Label _lblBatteryElec;

    private Label _lblMode;

    private Label _lblTemp;

    private Label _lblMissionTask;
    private ProgressBar _pbMission;
    private Label _lblMissionPct;
    private Label _lblMissionCmd;

    private ScrollView _faultList;
    private Label _lblFaultCount;
    private Label _lblUptime;
    private Label _lblDatetime;

    private MqttManager _mqtt;

    // ── 模式显示映射 ─────────────────────────────────────────
    private static readonly string[] ModeClasses =
        { "mode-autonomous", "mode-manual", "mode-remote", "mode-idle", "mode-error" };

    void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        var centerView = root.Q<VisualElement>("center-view");
        if (centerView != null) centerView.pickingMode = PickingMode.Ignore;

        BindElements(root);

        _mqtt = FindFirstObjectByType<MqttManager>();
        if (_mqtt != null)
        {
            _mqtt.OnConnected    += () => SetConnected(true);
            _mqtt.OnDisconnected += () => SetConnected(false);
            _mqtt.OnStatusUpdated += OnStatusUpdated;
        }
    }

    void Update()
    {
        if (_lblDatetime != null)
            _lblDatetime.text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // ── 绑定元素 ─────────────────────────────────────────────

    private void BindElements(VisualElement root)
    {
        _dotConn       = root.Q<VisualElement>("dot-conn");
        _lblConnStatus = root.Q<Label>("lbl-conn-status");

        _pbBattery      = root.Q<ProgressBar>("pb-battery");
        _lblBatteryPct  = root.Q<Label>("lbl-battery-pct");
        _lblBatteryElec = root.Q<Label>("lbl-battery-elec");

        _lblMode = root.Q<Label>("lbl-mode");

        _lblTemp = root.Q<Label>("lbl-temp");

        _lblMissionTask = root.Q<Label>("lbl-mission-task");
        _pbMission      = root.Q<ProgressBar>("pb-mission");
        _lblMissionPct  = root.Q<Label>("lbl-mission-pct");
        _lblMissionCmd  = root.Q<Label>("lbl-mission-cmd");

        _faultList      = root.Q<ScrollView>("fault-list");
        _lblFaultCount  = root.Q<Label>("lbl-fault-count");
        _lblUptime      = root.Q<Label>("lbl-uptime");
        _lblDatetime = root.Q<Label>("lbl-datetime");
    }

    // ── 连接状态 ─────────────────────────────────────────────

    private void SetConnected(bool connected)
    {
        if (_dotConn != null)
        {
            _dotConn.RemoveFromClassList("status-dot-green");
            _dotConn.RemoveFromClassList("status-dot-red");
            _dotConn.AddToClassList(connected ? "status-dot-green" : "status-dot-red");
        }
        if (_lblConnStatus != null)
            _lblConnStatus.text = connected ? "通信正常" : "通信断开";
    }

    // ── 系统状态更新 ─────────────────────────────────────────

    private void OnStatusUpdated(SystemStatusMsg data)
    {
        UpdatePower(data.system_status?.power);
        UpdateMode(data.system_status?.mode);
        UpdateTemperature(data.system_status?.temperature);
        UpdateUptime(data.system_status?.uptime ?? -1);
        UpdateMission(data.mission_status);
        UpdateFaults(data.faults);
    }

    private void UpdatePower(PowerStatus power)
    {
        if (power == null)
        {
            SetSafe(_lblBatteryPct,  "--%");
            SetSafe(_lblBatteryElec, "--V  --A");
            if (_pbBattery != null) _pbBattery.value = 0f;
            return;
        }
        float pct = Mathf.Clamp(power.battery_level, 0f, 100f);
        if (_pbBattery != null)  _pbBattery.value = pct;
        SetSafe(_lblBatteryPct,  $"{pct:0}%");
        SetSafe(_lblBatteryElec, $"{power.voltage:0.0}V  {power.current:0.0}A");

        // 电量低时变红
        if (_pbBattery != null)
        {
            var fill = _pbBattery.Q<VisualElement>(className: "unity-progress-bar__progress");
            if (fill != null)
                fill.style.backgroundColor = pct < 20f
                    ? new StyleColor(new Color(0.9f, 0.27f, 0.23f))
                    : new StyleColor(new Color(0.208f, 0.78f, 0.349f));
        }
    }

    private void UpdateMode(string mode)
    {
        if (_lblMode == null) return;

        foreach (var cls in ModeClasses)
            _lblMode.RemoveFromClassList(cls);

        if (string.IsNullOrEmpty(mode))
        {
            _lblMode.text = "--";
            _lblMode.AddToClassList("mode-idle");
            return;
        }

        string badgeClass;
        string displayText;
        switch (mode.ToLower())
        {
            case "autonomous": badgeClass = "mode-autonomous"; displayText = "自主"; break;
            case "manual":     badgeClass = "mode-manual";     displayText = "手动"; break;
            case "remote":     badgeClass = "mode-remote";     displayText = "遥控"; break;
            case "idle":       badgeClass = "mode-idle";       displayText = "待机"; break;
            case "error":      badgeClass = "mode-error";      displayText = "故障"; break;
            default:           badgeClass = "mode-idle";       displayText = mode;   break;
        }
        _lblMode.text = displayText;
        _lblMode.AddToClassList(badgeClass);
    }

    private void UpdateTemperature(TemperatureStatus temp)
    {
        if (_lblTemp == null) return;
        if (temp == null)
        {
            _lblTemp.text = "马达:--  控制:--  液压:--";
            return;
        }
        _lblTemp.text = $"马达:{temp.motor:0}°  控制:{temp.controller:0}°  液压:{temp.hydraulic:0}°";
    }

    private void UpdateUptime(int seconds)
    {
        if (_lblUptime == null) return;
        if (seconds < 0) { _lblUptime.text = "--:--:--"; return; }
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;
        _lblUptime.text = $"{h:00}:{m:00}:{s:00}";
    }

    private void UpdateMission(MissionStatus mission)
    {
        if (mission == null)
        {
            SetSafe(_lblMissionTask, "无任务");
            SetSafe(_lblMissionPct,  "--%");
            SetSafe(_lblMissionCmd,  "--");
            if (_pbMission != null) _pbMission.value = 0f;
            return;
        }
        float pct = Mathf.Clamp(mission.progress, 0f, 100f);
        SetSafe(_lblMissionTask, string.IsNullOrEmpty(mission.current_task) ? "无任务" : mission.current_task);
        if (_pbMission != null) _pbMission.value = pct;
        SetSafe(_lblMissionPct,  $"{pct:0}%");
        SetSafe(_lblMissionCmd,  string.IsNullOrEmpty(mission.command_id) ? "--" : mission.command_id);
    }

    private void UpdateFaults(FaultItem[] faults)
    {
        if (_faultList == null) return;

        _faultList.Clear();

        if (faults == null || faults.Length == 0)
        {
            var empty = new Label("系统正常，无故障记录");
            empty.AddToClassList("fault-empty");
            _faultList.Add(empty);
            SetFaultCountBadge(0, "ok");
            return;
        }

        // 找到最高级别，用于决定计数徽标颜色
        int maxLevel = 0;
        foreach (var f in faults)
            maxLevel = Mathf.Max(maxLevel, SeverityLevel(f.severity));

        string badgeSeverity = maxLevel switch { 3 => "critical", 2 => "error", 1 => "warning", _ => "ok" };
        SetFaultCountBadge(faults.Length, badgeSeverity);

        foreach (var fault in faults)
        {
            string sev = (fault.severity ?? "info").ToLower();

            var item = new VisualElement();
            item.AddToClassList("fault-item");

            var dot = new VisualElement();
            dot.AddToClassList("fault-item-dot");
            dot.AddToClassList($"fault-dot-{sev}");
            item.Add(dot);

            var code = new Label(fault.code ?? "---");
            code.AddToClassList("fault-item-code");
            item.Add(code);

            var msg = new Label(fault.message ?? "（无描述）");
            msg.AddToClassList("fault-item-msg");
            item.Add(msg);

            _faultList.Add(item);
        }
    }

    private void SetFaultCountBadge(int count, string severity)
    {
        if (_lblFaultCount == null) return;
        _lblFaultCount.text = count.ToString();
        _lblFaultCount.RemoveFromClassList("fault-count-ok");
        _lblFaultCount.RemoveFromClassList("fault-count-warning");
        _lblFaultCount.RemoveFromClassList("fault-count-error");
        _lblFaultCount.RemoveFromClassList("fault-count-critical");
        _lblFaultCount.AddToClassList($"fault-count-{severity}");
    }

    // ── 工具方法 ─────────────────────────────────────────────

    private static int SeverityLevel(string severity) => severity?.ToLower() switch
    {
        "warning"  => 1,
        "error"    => 2,
        "critical" => 3,
        _          => 0
    };

    private static void SetSafe(Label lbl, string text)
    {
        if (lbl != null) lbl.text = text;
    }
}
