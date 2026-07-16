using System;

/// <summary>
/// 高程图 MQTT 消息结构，与后端 JSON 对应。
/// </summary>
[Serializable]
public class ElevationMsg
{
    public double timestamp;
    public long sequence;
    public ElevationMetadata metadata;
    public string data_type;
    public string data_order;
    public string layer;
    public int[] data;
}

[Serializable]
public class ElevationMetadata
{
    public int width;
    public int height;
    public float resolution;
    public float height_resolution;
    public ElevationOrigin origin;
    public string origin_type;
    public string coordinate_system;
    public string frame_id;
    public float min_elevation;
    public float max_elevation;
    public int invalid_value = -32768;

    // --- Tiling support (optional) ---
    // If provided, Unity can stream multiple Terrain tiles without changing the topic name.
    public int tile_x;
    public int tile_y;
    public float tile_size_meters;
}

[Serializable]
public class ElevationOrigin
{
    public float x;
    public float y;
    public float z;
}
