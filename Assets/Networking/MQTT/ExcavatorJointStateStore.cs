using System;
using UnityEngine;

/// <summary>
/// 一次 MQTT 关节角度快照。对象不可变，读取方不会意外修改全局状态。
/// </summary>
public readonly struct ExcavatorJointAngles
{
    public float Boom { get; }
    public float Stick { get; }
    public float Bucket { get; }
    public float Rotate { get; }
    public bool HasRotate { get; }
    public double Timestamp { get; }

    public ExcavatorJointAngles(
        float boom,
        float stick,
        float bucket,
        float rotate,
        bool hasRotate,
        double timestamp)
    {
        Boom = boom;
        Stick = stick;
        Bucket = bucket;
        Rotate = rotate;
        HasRotate = hasRotate;
        Timestamp = timestamp;
    }
}

/// <summary>
/// 保存最新关节角度的进程内 Store。
/// 写入由 MqttManager 负责；其他模块通过 TryGetLatest 或 Changed 消费数据。
/// </summary>
public static class ExcavatorJointStateStore
{
    private static readonly object SyncRoot = new object();
    private static ExcavatorJointAngles _latest;
    private static bool _hasLatest;

    public static event Action<ExcavatorJointAngles> Changed;

    public static bool TryGetLatest(out ExcavatorJointAngles angles)
    {
        lock (SyncRoot)
        {
            angles = _latest;
            return _hasLatest;
        }
    }

    internal static void Publish(ExcavatorJointAngles angles)
    {
        Action<ExcavatorJointAngles> changed;
        lock (SyncRoot)
        {
            _latest = angles;
            _hasLatest = true;
            changed = Changed;
        }

        changed?.Invoke(angles);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        lock (SyncRoot)
        {
            _latest = default;
            _hasLatest = false;
            Changed = null;
        }
    }
}
