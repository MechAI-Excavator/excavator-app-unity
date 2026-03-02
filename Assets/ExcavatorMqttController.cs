using UnityEngine;

/// <summary>
/// 挖掘机 MQTT 控制示例：
/// 订阅传感器数据，发布控制指令。
/// 需要场景中有挂载 MqttManager 的 GameObject。
/// </summary>
public class ExcavatorMqttController : MonoBehaviour
{
    private MqttManager mqtt;

    void Start()
    {
        mqtt = FindFirstObjectByType<MqttManager>();
        if (mqtt == null)
        {
            Debug.LogError("[Excavator] 场景中找不到 MqttManager！");
            return;
        }

        // 监听收到的消息
        mqtt.OnMessageReceived += HandleMessage;
        mqtt.OnConnected += () => Debug.Log("[Excavator] MQTT 连接就绪");
        mqtt.OnDisconnected += () => Debug.LogWarning("[Excavator] MQTT 断开");
    }

    void OnDestroy()
    {
        if (mqtt != null)
            mqtt.OnMessageReceived -= HandleMessage;
    }

    // ── 接收消息 ─────────────────────────────────────────────

    private void HandleMessage(string topic, string message)
    {
        Debug.Log($"[Excavator] {topic} → {message}");

        switch (topic)
        {
            case "excavator/status":
                HandleStatus(message);
                break;
            case "excavator/sensor":
                HandleSensor(message);
                break;
        }
    }

    private void HandleStatus(string json)
    {
        // 示例：解析 JSON 状态
        // var status = JsonUtility.FromJson<ExcavatorStatus>(json);
        Debug.Log($"[Excavator] 状态更新: {json}");
    }

    private void HandleSensor(string json)
    {
        Debug.Log($"[Excavator] 传感器数据: {json}");
    }

    // ── 发布控制指令（可绑定到 UI 按钮）─────────────────────

    public void SendArmUp()
    {
        mqtt?.Publish("excavator/control", "{\"cmd\":\"arm\",\"dir\":\"up\"}");
    }

    public void SendArmDown()
    {
        mqtt?.Publish("excavator/control", "{\"cmd\":\"arm\",\"dir\":\"down\"}");
    }

    public void SendRotate(float angle)
    {
        mqtt?.Publish("excavator/control", $"{{\"cmd\":\"rotate\",\"angle\":{angle}}}");
    }

    public void SendStop()
    {
        mqtt?.Publish("excavator/control", "{\"cmd\":\"stop\"}");
    }
}
