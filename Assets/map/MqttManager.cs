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
    private static readonly string[] JointsTopicAliases = { "01/joints", "joints" };

    [Header("Broker Settings")]
    [Tooltip("MQTT Broker 地址，例如 192.168.1.100 或 broker.hivemq.com")]
    public string brokerHost = "127.0.0.1";

    [Tooltip("MQTT Broker 端口，默认 1883（TLS 为 8883）")]
    public int brokerPort = 1883;

    [Tooltip("客户端 ID，留空则自动生成")]
    public string clientId = "";

    [Header("Connection Reliability")]
    [Tooltip("连接意外关闭或首次连接失败后自动重连")]
    public bool autoReconnect = true;

    [Min(0.5f)]
    [Tooltip("自动重连间隔（秒）")]
    public float reconnectDelaySeconds = 2f;

    [Header("Auth (Optional)")]
    public string username = "";
    public string password = "";

    [Header("Topics")]
    [Tooltip("启动时自动订阅的主题列表")]
    public string[] subscribeTopics = { "excavator/sensor", "01/map/elevation", "01/sensor/rtk_lio", "01/joints" };

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
    private bool _loggedFirstJointsMessage;
    private bool _isConnecting;
    private bool _isShuttingDown;
    private volatile bool _connectionClosedPending;
    private float _nextReconnectAt;

    void Start()
    {
        _excavatorController = FindFirstObjectByType<ExcavatorController>();
        Connect();
    }

    void Update()
    {
        if (_connectionClosedPending)
        {
            _connectionClosedPending = false;
            Debug.LogWarning("[MQTT] 连接已关闭");
            OnDisconnected?.Invoke();
            ScheduleReconnect();
        }

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

        if (autoReconnect
            && !_isShuttingDown
            && !_isConnecting
            && !IsConnected
            && Time.unscaledTime >= _nextReconnectAt)
        {
            Connect();
        }
    }

    void OnDestroy()
    {
        _isShuttingDown = true;
        Disconnect();
    }

    // ── 公开 API ─────────────────────────────────────────────

    public void Connect()
    {
        if (_isConnecting || IsConnected || _isShuttingDown)
            return;

        _isConnecting = true;
        try
        {
            ReleaseClient(false);

            string deviceId = SystemInfo.deviceUniqueIdentifier;
            string shortDeviceId = string.IsNullOrEmpty(deviceId)
                ? Guid.NewGuid().ToString("N")[..8]
                : deviceId[..Math.Min(8, deviceId.Length)];
            string id = string.IsNullOrEmpty(clientId)
                ? "unity-" + shortDeviceId
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
                _nextReconnectAt = 0f;
            }
            else
            {
                Debug.LogError($"[MQTT] 连接被拒绝，返回码: {code}");
                ReleaseClient(false);
                ScheduleReconnect();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 连接失败: {e.Message}");
            ReleaseClient(false);
            ScheduleReconnect();
        }
        finally
        {
            _isConnecting = false;
        }
    }

    public void Disconnect()
    {
        bool wasConnected = IsConnected;
        ReleaseClient(true);
        if (wasConnected)
            Debug.Log("[MQTT] 已断开连接");
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
        var topics = new List<string>();
        if (subscribeTopics != null)
        {
            foreach (string topic in subscribeTopics)
            {
                if (!string.IsNullOrWhiteSpace(topic) && !topics.Contains(topic))
                    topics.Add(topic);
            }
        }

        foreach (string topic in JointsTopicAliases)
        {
            if (!topics.Contains(topic))
                topics.Add(topic);
        }

        var qosLevels = new byte[topics.Count];
        for (int i = 0; i < qosLevels.Length; i++)
            qosLevels[i] = MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE;

        client.Subscribe(topics.ToArray(), qosLevels);
        Debug.Log($"[MQTT] 已订阅 {topics.Count} 个主题: {string.Join(", ", topics)}");
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
            case "01/joints":
            case "joints":
                HandleJoints(topic, msg);
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
        // M2Mqtt 在后台线程回调；Unity 日志、事件和重连都交给主线程 Update。
        _connectionClosedPending = true;
    }

    private void ScheduleReconnect()
    {
        if (!autoReconnect || _isShuttingDown)
            return;

        _nextReconnectAt =
            Time.unscaledTime + Mathf.Max(0.5f, reconnectDelaySeconds);
    }

    private void ReleaseClient(bool disconnect)
    {
        var oldClient = client;
        client = null;
        if (oldClient == null)
            return;

        oldClient.MqttMsgPublishReceived -= OnMqttMessageReceived;
        oldClient.ConnectionClosed -= OnConnectionClosed;

        if (!disconnect || !oldClient.IsConnected)
            return;

        try
        {
            oldClient.Disconnect();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MQTT] 断开连接时发生异常: {e.Message}");
        }
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

    private void HandleJoints(string topic, string msg)
    {
        try
        {
            var data = JsonUtility.FromJson<JointsMsg>(msg);
            if (data?.joints?.boom == null
                || data.joints.stick == null
                || data.joints.bucket == null)
            {
                Debug.LogWarning(
                    $"[MQTT] {topic} 解析失败，期望 joints.boom/stick/bucket.angle；" +
                    $"payload={msg}");
                return;
            }

            var angles = new ExcavatorJointAngles(
                data.joints.boom.angle,
                data.joints.stick.angle,
                data.joints.bucket.angle,
                data.timestamp);
            ExcavatorJointStateStore.Publish(angles);

            if (!_loggedFirstJointsMessage)
            {
                _loggedFirstJointsMessage = true;
                Debug.Log(
                    $"[MQTT] 已接收首帧关节数据 topic={topic} " +
                    $"boom={angles.Boom:F3} stick={angles.Stick:F3} " +
                    $"bucket={angles.Bucket:F3}");
            }

            if (_excavatorController == null)
                _excavatorController = FindFirstObjectByType<ExcavatorController>();

            if (_excavatorController != null)
            {
                // The reported angles are relative to each joint's parent link. Feeding
                // them to the articulation drives lets Unity evaluate forward kinematics.
                // Cabin and velocity are intentionally ignored for now.
                _excavatorController.ApplyJointAngles(
                    angles.Boom,
                    angles.Stick,
                    angles.Bucket);
            }
            else
            {
                Debug.LogWarning("[MQTT] 场景中未找到 ExcavatorController，无法将 01/joints 应用到 3D 模型");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MQTT] 01/joints 解析异常: {e.Message}");
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

// ── 关节正运动学消息 ─────────────────────────────────────────

[Serializable]
public class JointKinematicsState
{
    public float angle;
    public float velocity;
}

[Serializable]
public class JointKinematicsPayload
{
    public JointKinematicsState bucket;
    public JointKinematicsState stick;
    public JointKinematicsState boom;
    public JointKinematicsState cabin;
}

[Serializable]
public class JointsMsg
{
    public double timestamp;
    public JointKinematicsPayload joints;
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
