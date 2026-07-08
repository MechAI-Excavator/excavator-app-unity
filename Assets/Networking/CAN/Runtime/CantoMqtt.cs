using System;
using UnityEngine; 

/// <summary>
/// 从 SocketCanUnity 获取 PWM（6 通道），映射为 JointControlMsg 并通过 MqttManager 发布 JSON。
/// - 默认映射： pwm[0]->bucket, pwm[1]->boom, pwm[2]->stick, pwm[3]->cabin, pwm[4]->leftTrack, pwm[5]->rightTrack
/// - 可选：将 PWM(0..1000) 线性缩放到 angleRange（minAngle..maxAngle）
/// - 支持事件回调订阅或主线程轮询 LastPwm（如果没有事件或未赋值）
/// </summary>
public class CantoMqtt : MonoBehaviour
{
    [Header("References (can drag)")]
    public SocketCanUnity socketCan;      // 可在 Inspector 指定；若为空会尝试在 Awake/Start 中查找
    public MqttManager mqttManager;       // 可在 Inspector 指定；若为空会尝试在 Start 中 Find 
    [Header("MQTT")]
    public string publishTopic = "excavator_001/joint_control";
    public bool publishOnReceive = true;  // 使用 OnPwmFrame 事件触发发布；否则在 Update 以轮询方式发布（限频）
    [Tooltip("每秒最大发布次数（限频），当使用事件时也适用以防洪水式发布")]
    public float publishRateHz = 100f;
    
    [Header("Mapping / Scaling")]
    public bool enableScaling = false;    // 是否把 PWM 映射到角度范围
    public float minAngle = -30f;         // scaling: PWM=0 -> minAngle
    public float maxAngle = 30f;          // scaling: PWM=1000 -> maxAngle
    
    // internal
    double lastPublishTime = 0.0;
    // ushort[] lastPwmSnapshot = null;
    
    void Awake()
    {
        if (socketCan == null)
            socketCan = FindObjectOfType<SocketCanUnity>();
    
        // don't auto-get mqtt here because it might not be ready; do in Start
    }
    
    void Start()
    {
        if (mqttManager == null)
            mqttManager = FindObjectOfType<MqttManager>();
    
        if (socketCan == null)
        {
            Debug.LogWarning("MqttPwmPublisher: SocketCanUnity not found in scene. Publisher will be inactive.");
            enabled = false;
            return;
        }
    
        if (publishOnReceive)
        {
            // subscribe to event (SocketCanUnity invokes OnPwmFrame on main thread)
            socketCan.OnPwmFrame += OnPwmFrameReceived;
        }
    
        lastPublishTime = Time.realtimeSinceStartupAsDouble - (1.0 / Math.Max(0.0001, publishRateHz));
    }
    
    void OnEnable()
    {
        // if script is enabled after Start and publishOnReceive true, ensure subscription
        if (socketCan != null && publishOnReceive)
            socketCan.OnPwmFrame += OnPwmFrameReceived;
    }
    
    void OnDisable()
    {
        if (socketCan != null)
            socketCan.OnPwmFrame -= OnPwmFrameReceived;
    }
    
    void Update()
    {
        // If not using event or fallback, poll LastPwm at rate
        if (!publishOnReceive)
        {
            if (socketCan == null) return;
            var pwm = socketCan.LastPwm;
            if (pwm != null)
            {
                // avoid publishing identical references too fast: snapshot and publish by rate
                PublishIfAllowed(pwm);
            }
        }
    }
    
    void OnPwmFrameReceived(ushort[] pwm)
    {
        // OnPwmFrame is invoked on main thread by your SocketCanUnity implementation.
        // We may get high rates; enforce publish rate limit.
        PublishIfAllowed(pwm);
    }
    
    void PublishIfAllowed(ushort[] pwm)
    {
        if (pwm == null) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now - lastPublishTime < 1.0 / Math.Max(0.0001, publishRateHz))
            return;
        lastPublishTime = now;
    
        // create message and publish
        var msg = BuildJointControlFromPwm(pwm);
        string json = JsonUtility.ToJson(msg);
        if (mqttManager != null && mqttManager.IsConnected)
        {
            try
            {
                mqttManager.Publish(publishTopic, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"MqttPwmPublisher publish failed: {ex.Message}");
            }
        }
        else
        {
            Debug.LogWarning("MqttPwmPublisher: MqttManager not found or not connected; skipping publish.");
        }
    }
    
    JointControlMsg BuildJointControlFromPwm(ushort[] pwm)
    {
        // Build JointControlMsg and map pwm -> joints
        var msg = new JointControlMsg();
        msg.timestamp = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
    
        var payload = new JointsPayload();
    
        // Map channels to joints. Ensure pwm array has enough entries
        float cabinPwm = GetMappedAngle(pwm, 3);
        float boomPwm = GetMappedAngle(pwm, 1);
        float stickPwm = GetMappedAngle(pwm, 2);
        float bucketPwm = GetMappedAngle(pwm, 0);
        float leftTrackPwm = GetMappedAngle(pwm, 4);
        float rightTrackPwm = GetMappedAngle(pwm, 5);
    
        payload.cabin = new JointState { pwm = cabinPwm};
        payload.boom = new JointState { pwm = boomPwm };
        payload.stick = new JointState { pwm = stickPwm };
        payload.bucket = new JointState { pwm = bucketPwm };
        payload.leftTrack = new JointState { pwm = leftTrackPwm };
        payload.rightTrack = new JointState { pwm = rightTrackPwm };
    
        msg.joints = payload;
        return msg;
    }
    
    float GetMappedAngle(ushort[] pwm, int idx)
    {
        if (pwm == null || idx < 0 || idx >= pwm.Length) return 0f;
        float v = pwm[idx];
        if (!enableScaling) return v; // direct PWM value as float
        float t = Mathf.Clamp01(v / 1000f);
        return Mathf.Lerp(minAngle, maxAngle, t);
    }
    
} 