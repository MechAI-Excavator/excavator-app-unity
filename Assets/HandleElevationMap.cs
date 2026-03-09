using UnityEngine;

public class HandleElevationMap : MonoBehaviour
{
    [Tooltip("留空则使用 TerrainData 所在 Terrain")]
    public Terrain terrain;
    [Tooltip("若 Terrain 已赋值，可留空")]
    public TerrainData terrainData;

    [Header("高度着色")]
    [Tooltip("高度着色渐变，从低（左）到高（右），可在 Inspector 中自定义")]
    public Gradient elevationGradient;

    [Tooltip("着色层数，越多过渡越平滑")]
    [Range(4, 8)]
    public int colorBands = 6;

    void Reset()
    {
        elevationGradient = CreateDefaultGradient();
    }

    void Awake()
    {
        EnsureGradient();
    }

    void EnsureGradient()
    {
        if (elevationGradient == null || IsDefaultWhiteGradient(elevationGradient))
            elevationGradient = CreateDefaultGradient();
    }

    static bool IsDefaultWhiteGradient(Gradient g)
    {
        Color c = g.Evaluate(0.5f);
        return c.r > 0.95f && c.g > 0.95f && c.b > 0.95f;
    }

    static Gradient CreateDefaultGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.50f, 0.00f, 0.75f), 0.00f),  // 紫色 — 最低
                new GradientColorKey(new Color(0.00f, 0.10f, 0.65f), 0.20f),  // 深蓝
                new GradientColorKey(new Color(0.00f, 0.40f, 1.00f), 0.40f),  // 蓝色 — 平面基准
                new GradientColorKey(new Color(0.15f, 0.80f, 0.15f), 0.65f),  // 绿色
                new GradientColorKey(new Color(0.90f, 0.70f, 0.00f), 0.85f),  // 橙黄
                new GradientColorKey(new Color(0.85f, 0.12f, 0.00f), 1.00f),  // 红色 — 最高
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
        );
        return g;
    }

    /// <summary>由 MqttManager 在收到 01/map/elevation 时调用</summary>
    public void OnElevationDataReceived(ElevationMsg msg)
    {
        if (msg.data_type != "int16") return;

        int w = msg.metadata.width;
        int h = msg.metadata.height;
        int total = w * h;
        const int NODATA = -32768;

        // ── 第一遍：扫描实际 min/max（跳过 nodata） ──
        int rawMin = int.MaxValue;
        int rawMax = int.MinValue;
        for (int i = 0; i < total; i++)
        {
            int v = msg.data[i];
            if (v == NODATA) continue;
            if (v < rawMin) rawMin = v;
            if (v > rawMax) rawMax = v;
        }
        if (rawMin > rawMax) { rawMin = 0; rawMax = 1; }

        float hr = msg.metadata.height_resolution;
        float actualMin = rawMin * hr;
        float actualMax = rawMax * hr;
        float range = actualMax - actualMin;
        if (range <= 0f) range = 1f;
        float scale = 1f / range;

        // ── 第二遍：生成 heightmap + normalizedMap ──
        float[,] heights = new float[h + 1, w + 1];
        float[,] normalizedMap = new float[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int raw = msg.data[y * w + x];
                if (raw == NODATA) raw = rawMin;

                float meters = raw * hr;
                float normalized = Mathf.Clamp01((meters - actualMin) * scale);

                normalizedMap[y, x] = normalized;

                float heightVal = Mathf.Clamp01(normalized * 0.2f);
                heights[y, x] = heightVal;
                if (y == h - 1) heights[y + 1, x] = heightVal;
                if (x == w - 1) heights[y, x + 1] = heightVal;
                if (y == h - 1 && x == w - 1) heights[y + 1, x + 1] = heightVal;
            }
        }

        var td = terrain != null ? terrain.terrainData : terrainData;
        if (td == null)
        {
            Debug.LogWarning("[HandleElevationMap] 未设置 Terrain 或 TerrainData");
            return;
        }

        td.SetHeights(0, 0, heights);
        EnsureGradient();
        InitTerrainLayers(td);
        ApplyElevationColors(td, normalizedMap, w, h);

        Debug.Log($"[HandleElevationMap] 实际高程范围: {actualMin:F2}m ~ {actualMax:F2}m");
    }

    void InitTerrainLayers(TerrainData td)
    {
        var layers = new TerrainLayer[colorBands];
        for (int i = 0; i < colorBands; i++)
        {
            float t = (float)i / (colorBands - 1);
            Color col = elevationGradient.Evaluate(t);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var px = new[] { col, col, col, col };
            tex.SetPixels(px);
            tex.Apply(false, true);

            var layer = new TerrainLayer();
            layer.diffuseTexture = tex;
            layer.tileSize = new Vector2(td.size.x, td.size.z);
            layers[i] = layer;
        }
        td.terrainLayers = layers;
    }

    void ApplyElevationColors(TerrainData td, float[,] normalizedMap, int dataW, int dataH)
    {
        int alphaW = td.alphamapWidth;
        int alphaH = td.alphamapHeight;
        int numLayers = td.terrainLayers.Length;

        var alphamap = new float[alphaH, alphaW, numLayers];

        for (int ay = 0; ay < alphaH; ay++)
        {
            for (int ax = 0; ax < alphaW; ax++)
            {
                int dx = Mathf.Clamp((int)((float)ax / alphaW * dataW), 0, dataW - 1);
                int dy = Mathf.Clamp((int)((float)ay / alphaH * dataH), 0, dataH - 1);

                float n = normalizedMap[dy, dx];
                float bandPos = n * (numLayers - 1);
                int lo = Mathf.Clamp(Mathf.FloorToInt(bandPos), 0, numLayers - 1);
                int hi = Mathf.Clamp(Mathf.CeilToInt(bandPos), 0, numLayers - 1);
                float blend = bandPos - lo;

                if (lo == hi)
                {
                    alphamap[ay, ax, lo] = 1f;
                }
                else
                {
                    alphamap[ay, ax, lo] = 1f - blend;
                    alphamap[ay, ax, hi] = blend;
                }
            }
        }

        td.SetAlphamaps(0, 0, alphamap);
    }
}