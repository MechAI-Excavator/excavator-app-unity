using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

/// <summary>
/// MQTT 连接管理器，挂载到场景中的 GameObject 上使用。
/// 依赖：Assets/Plugins/ 下需有 M2Mqtt.dll
/// </summary>
public class MqttManager : MonoBehaviour
{
    [Header("Broker Settings")]
    [Tooltip("MQTT Broker 地址，例如 192.168.1.100 或 broker.hivemq.com")]
    public string brokerHost = "127.0.0.1";

    [Tooltip("MQTT Broker 端口，默认 1883（TLS 为 8883）")]
    public int brokerPort = 1883;

    [Tooltip("客户端 ID，留空则自动生成")]
    public string clientId = "";

    [Header("Auth (Optional)")]
    public string username = "";
    public string password = "";

    [Header("Topics")]
    [Tooltip("启动时自动订阅的主题列表")]
    public string[] subscribeTopics = { "excavator/sensor", "01/map/elevation", "01/sensor/rtk_lio", "01/joint_control" };

    [Tooltip("发布数据的默认主题")]
    public string publishTopic = "excavator_001/control";

    // 连接状态
    public bool IsConnected => client != null && client.IsConnected;

    // 收到消息时触发，参数为 (topic, message)
    public event Action<string, string> OnMessageReceived;

    // 连接成功/断开时触发
    public event Action OnConnected;
    public event Action OnDisconnected;
    public event Action<RtkGpsMsg> OnRtkUpdated;
    public event Action<SystemStatusMsg> OnStatusUpdated;

    private MqttClient client;
    private readonly Queue<(string topic, string msg)> messageQueue = new();
    private readonly object queueLock = new();
    private ExcavatorController _excavatorController;

    void Start()
    {
        _excavatorController = FindFirstObjectByType<ExcavatorController>();
        Connect();
    }

    void Update()
    {
        // 在主线程中派发收到的消息（M2Mqtt 回调在子线程）
        lock (queueLock)
        {
            while (messageQueue.Count > 0)
            {
                var (topic, msg) = messageQueue.Dequeue();
                OnMessageReceived?.Invoke(topic, msg);
                DispatchByTopic(topic, msg);
            }
        }
    }

    void OnDestroy()
    {
        Disconnect();
    }

    // ── 公开 API ─────────────────────────────────────────────

    public void Connect()
    {
        try
        {
            string id = string.IsNullOrEmpty(clientId)
                ? "unity-" + SystemInfo.deviceUniqueIdentifier[..8]
                : clientId;

            client = new MqttClient(brokerHost, brokerPort, false, null, null,
                MqttSslProtocols.None);

            client.MqttMsgPublishReceived += OnMqttMessageReceived;
            client.ConnectionClosed += OnConnectionClosed;

            byte code = string.IsNullOrEmpty(username)
                ? client.Connect(id)
                : client.Connect(id, username, password);

            if (code == MqttMsgConnack.CONN_ACCEPTED)
            {
                Debug.Log($"[MQTT] 已连接到 {brokerHost}:{brokerPort}");
                SubscribeAll();
                OnConnected?.Invoke();
            }
            else
            {
                Debug.LogError($"[MQTT] 连接被拒绝，返回码: {code}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 连接失败: {e.Message}");
        }
    }

    public void Disconnect()
    {
        if (client != null && client.IsConnected)
        {
            client.Disconnect();
            Debug.Log("[MQTT] 已断开连接");
        }
    }

    /// <summary>发布字符串消息</summary>
    public void Publish(string topic, string message, bool retain = false)
    {
        if (!IsConnected)
        {
            Debug.LogWarning("[MQTT] 未连接，无法发布消息");
            return;
        }
        byte[] payload = Encoding.UTF8.GetBytes(message);
        Debug.Log($"Publishing to {topic}: {message}");
        int msgId =client.Publish(topic, payload, MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, retain);
        Debug.Log($"Published topic={topic} msgId={msgId} qos=1 retain={retain}");
    }

    /// <summary>发布到默认主题</summary>
    public void Publish(string message) => Publish(publishTopic, message);

    /// <summary>动态订阅主题</summary>
    public void Subscribe(string topic)
    {
        if (!IsConnected) return;
        client.Subscribe(new[] { topic },
            new[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
        Debug.Log($"[MQTT] 已订阅: {topic}");
    }

    // ── 内部方法 ─────────────────────────────────────────────

    private void SubscribeAll()
    {
        if (subscribeTopics == null || subscribeTopics.Length == 0) return;
        var qosLevels = new byte[subscribeTopics.Length];
        for (int i = 0; i < qosLevels.Length; i++)
            qosLevels[i] = MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE;

        client.Subscribe(subscribeTopics, qosLevels);
        Debug.Log($"[MQTT] 已订阅 {subscribeTopics.Length} 个主题");
    }

    private void OnMqttMessageReceived(object sender, MqttMsgPublishEventArgs e)
    {
        string topic = e.Topic;
        string msg = Encoding.UTF8.GetString(e.Message);
        lock (queueLock)
        {
            messageQueue.Enqueue((topic, msg));
        }
    }

    private void DispatchByTopic(string topic, string msg)
    {
        switch (topic)
        {
            case "01/map/elevation":
                HandleElevation(msg);
                break;
            case "01/status":
                HandleStatus(msg);
                break;
            case "01/sensor/rtk_lio":
                HandleRtkLio(msg);
                break;
            case "01/joint_control":
                HandleJointControl(msg);
                break;
        }
    }

    private void HandleStatus(string msg)
    {
        try
        {
            var status = JsonUtility.FromJson<SystemStatusMsg>(msg);
            if (status == null)
            {
                Debug.LogWarning("[MQTT] 系统状态数据解析失败或为空");
                return;
            }
            OnStatusUpdated?.Invoke(status);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 系统状态解析异常: {e.Message}");
        }
    }

    private void HandleSensor(string msg)
    {
        Debug.Log($"[MQTT] 传感器数据: {msg}");
    }

    private void OnConnectionClosed(object sender, EventArgs e)
    {
        Debug.LogWarning("[MQTT] 连接已关闭");
        OnDisconnected?.Invoke();
    }

    private void HandleRtkLio(string msg)
    {
        try
        {
            var rtk = JsonUtility.FromJson<RtkGpsMsg>(msg);
            if (rtk == null || rtk.rtk_status == null || rtk.position == null)
            {
                Debug.LogWarning("[MQTT] RTK GPS 数据解析失败或为空");
                return;
            }
            OnRtkUpdated?.Invoke(rtk);

            if (_excavatorController == null)
                _excavatorController = FindFirstObjectByType<ExcavatorController>();

            if (_excavatorController != null)
            {
                // Elevation metadata.origin now owns translation; RTK still owns orientation.
                _excavatorController.ApplyRtkRotation(rtk.rotation);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] RTK GPS 解析异常: {e.Message}");
        }
    }

    private void HandleJointControl(string msg)
    {
        try
        {
            var data = JsonUtility.FromJson<JointControlMsg>(msg);
            if (data?.joints == null)
            {
                Debug.LogWarning("[MQTT] 关节控制数据解析失败或 joints 为空");
                return;
            }

            if (_excavatorController == null)
                _excavatorController = FindFirstObjectByType<ExcavatorController>();

            if (_excavatorController != null)
            {
                _excavatorController.ApplyJointControl(
                    data.joints.cabin.pwm,
                    data.joints.boom.pwm,
                    data.joints.stick.pwm,
                    data.joints.bucket.pwm
                );
            }
            else
            {
                Debug.LogWarning("[MQTT] 场景中未找到 ExcavatorController，无法应用关节控制");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 关节控制解析异常: {e.Message}");
        }
    }

    private void HandleElevation(string msg)
    {
        try
        {
            var elevation = JsonUtility.FromJson<ElevationMsg>(msg);
            if (elevation?.metadata == null || elevation.data == null)
            {
                Debug.LogWarning("[MQTT] 高程图 JSON 解析失败或数据为空");
                return;
            }

            var metadata = elevation.metadata;
            Debug.Log($"[MQTT] 高程图 seq={elevation.sequence} " +
                      $"samples={metadata.width}x{metadata.height} " +
                      $"tile=({metadata.tile_x},{metadata.tile_y}) size={metadata.tile_size_meters:F2}m " +
                      $"origin=({metadata.origin?.x:F2},{metadata.origin?.y:F2},{metadata.origin?.z:F2})");

            var tileManager = FindFirstObjectByType<TerrainTileManager>();
            if (tileManager != null)
            {
                var terrain = tileManager.OnElevationTile(elevation);
                ApplyElevationOrigin(elevation, terrain);
                return;
            }

            var handler = FindFirstObjectByType<HandleElevationMap>();
            if (handler != null)
            {
                handler.OnElevationDataReceived(elevation);
                var terrain = handler.terrain != null ? handler.terrain : handler.GetComponent<Terrain>();
                ApplyElevationOrigin(elevation, terrain);
            }
            else
                Debug.LogWarning("[MQTT] 场景中未找到 HandleElevationMap，无法更新地形");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 高程图解析异常: {e.Message}");
        }
    }

    private void ApplyElevationOrigin(ElevationMsg elevation, Terrain terrain)
    {
        var metadata = elevation?.metadata;
        if (metadata?.origin == null)
            return; // Backward compatibility: old messages do not move the excavator.

        if (!string.IsNullOrEmpty(metadata.origin_type)
            && !string.Equals(metadata.origin_type, "center", StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogWarning($"[MQTT] 不支持高程图 origin_type={metadata.origin_type}，期望 center；已忽略本次位置更新");
            return;
        }

        if (terrain == null)
        {
            Debug.LogWarning("[MQTT] 已收到高程图 origin，但无法确定对应 Terrain，未更新挖掘机位置");
            return;
        }

        if (_excavatorController == null)
            _excavatorController = FindFirstObjectByType<ExcavatorController>();

        if (_excavatorController != null)
            _excavatorController.ApplyElevationOrigin(
                metadata.origin,
                terrain,
                metadata.coordinate_system);
        else
            Debug.LogWarning("[MQTT] 场景中未找到 ExcavatorController，无法应用高程图 origin");
    }
}

[Serializable]
public class JointState
{
    public float pwm;
}

[Serializable]
public class JointsPayload
{
    public JointState cabin;
    public JointState boom;
    public JointState stick;
    public JointState bucket;
    public JointState leftTrack;
    public JointState rightTrack;
}

[Serializable]
public class JointControlMsg
{
    public double timestamp;
    public JointsPayload joints;
}

// ── 系统状态消息 ─────────────────────────────────────────────

[Serializable]
public class PowerStatus
{
    public float battery_level;
    public float voltage;
    public float current;
}

[Serializable]
public class TemperatureStatus
{
    public float motor;
    public float controller;
    public float hydraulic;
}

[Serializable]
public class SystemStatus
{
    public PowerStatus power;
    public TemperatureStatus temperature;
    public string mode;
    public int uptime;
}

[Serializable]
public class FaultItem
{
    public string code;
    public string severity;
    public string message;
    public double timestamp;
}

[Serializable]
public class MissionStatus
{
    public string current_task;
    public float progress;
    public double estimated_completion;
    public string command_id;
}

[Serializable]
public class SystemStatusMsg
{
    public double timestamp;
    public SystemStatus system_status;
    public FaultItem[] faults;
    public MissionStatus mission_status;
}
