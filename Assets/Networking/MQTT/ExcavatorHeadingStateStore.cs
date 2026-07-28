using System;
using UnityEngine;

/// <summary>
/// 挖掘机的绝对地图朝向。HeadingDegrees 使用旧罗盘角约定：
/// 北=0°、东=90°、顺时针为正，便于 Unity 模型和屏幕方位指示直接使用。
/// </summary>
public readonly struct ExcavatorHeading
{
    public float HeadingDegrees { get; }
    public float EnuYawRadians { get; }
    public double Timestamp { get; }

    public ExcavatorHeading(
        float headingDegrees,
        float enuYawRadians,
        double timestamp)
    {
        HeadingDegrees = headingDegrees;
        EnuYawRadians = enuYawRadians;
        Timestamp = timestamp;
    }
}

/// <summary>
/// 01/map/elevation metadata.origin.yaw 的进程内 Store。
/// 写入发生在 Unity 主线程，UI 可在启用或重建时读取最新值。
/// </summary>
public static class ExcavatorHeadingStateStore
{
    private static readonly object SyncRoot = new object();
    private static ExcavatorHeading _latest;
    private static bool _hasLatest;

    public static event Action<ExcavatorHeading> Changed;

    /// <summary>
    /// ENU yaw（弧度，东=0，逆时针为正）转为罗盘角
    /// （度，北=0，顺时针为正）。
    /// </summary>
    public static float EnuYawToCompassDegrees(float yawRadians)
    {
        return Mathf.Repeat(90f - yawRadians * Mathf.Rad2Deg, 360f);
    }

    public static bool TryGetLatest(out ExcavatorHeading heading)
    {
        lock (SyncRoot)
        {
            heading = _latest;
            return _hasLatest;
        }
    }

    internal static void Publish(ExcavatorHeading heading)
    {
        Action<ExcavatorHeading> changed;
        lock (SyncRoot)
        {
            _latest = heading;
            _hasLatest = true;
            changed = Changed;
        }

        changed?.Invoke(heading);
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
