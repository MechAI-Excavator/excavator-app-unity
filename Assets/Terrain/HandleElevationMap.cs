using System.Collections;
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

    [Header("Performance")]
    [Tooltip("打开：按高度染色（建议开启）。关闭：只写高度，不做 alphamap，性能最好。")]
    public bool enableColoring = true;

    [Tooltip("是否每次都重建 TerrainLayer（非常耗时）。建议关闭，仅首次初始化。")]
    public bool rebuildTerrainLayersEveryUpdate = false;

    [Tooltip("使用 metadata.min_elevation / max_elevation 做归一化（跨 tile 颜色可比）。若全局范围远大于本 tile 实际高差，画面会几乎单色。")]
    public bool useGlobalRangeForColoring = false;

    [Tooltip("按本 tile 高度图 min/max 着色（推荐开启）：颜色与地表起伏一一对应，渐变明显。")]
    public bool colorFromHeightmapRange = true;

    [Header("Surface Look")]
    [Tooltip("在每个高度色带的贴图上叠加栅格线（更有工程/测绘质感）。")]
    public bool overlayGridTexture = true;

    [Tooltip("生成的栅格贴图分辨率（越大越细腻，但创建略慢；通常 64 或 128 足够）。")]
    [Range(16, 512)]
    public int gridTextureResolution = 128;

    [Tooltip("每隔多少像素画一条栅格线。数值越小，格子越密。")]
    [Range(4, 64)]
    public int gridLineEveryPixels = 4;

    [Tooltip("栅格线宽（像素）。")]
    [Range(1, 8)]
    public int gridLineWidthPixels = 1;

    [Tooltip("栅格线强度（0=无，1=很明显）。")]
    [Range(0f, 1f)]
    public float gridLineStrength = 0.5f;

    [Tooltip("在纯色底上加入少量噪声（0=无），避免过于塑料。")]
    [Range(0f, 1f)]
    public float surfaceNoiseStrength = 0.1f;

    [Tooltip("让栅格线更锐利（Point 采样），关闭则更平滑（Bilinear）。")]
    public bool crispGridLines = true;

    [Tooltip("栅格贴图在世界中每隔多少米重复一次（越小越密）。例如 2 表示每 2m 一组栅格纹理。")]
    [Range(0.5f, 25f)]
    public float gridRepeatMeters = 2f;

    [Tooltip("为地表自动生成法线贴图（建议开启，会显著降低“光滑假”的感觉）。")]
    public bool generateSurfaceNormalMap = true;

    [Tooltip("法线强度（越大越“粗糙”）。")]
    [Range(0f, 4f)]
    public float surfaceNormalStrength = 0.6f;

    [Tooltip("噪声的空间频率（越大噪点越密）。")]
    [Range(2f, 64f)]
    public float noiseFrequency = 22f;

    [Tooltip("叠加噪声层数（更多层更自然但略慢）。")]
    [Range(1, 4)]
    public int noiseOctaves = 2;

    [Tooltip("整体亮度系数（>1 更亮，<1 更暗）。")]
    [Range(0.5f, 2f)]
    public float surfaceBrightness = 1.15f;

    [Header("Real material (optional)")]
    [Tooltip("基础地表 Albedo（可平铺）。设置后将用真实贴图叠加高度分色 tint，而不是纯色底。")]
    public Texture2D baseAlbedo;

    [Tooltip("基础地表 Normal（可平铺，Texture Type 必须是 Normal map）。设置后将直接复用该法线贴图。")]
    public Texture2D baseNormal;

    [Tooltip("高度分色 tint 强度：0=完全原始土贴图，1=完全用渐变色覆盖。建议 0.25~0.55。")]
    [Range(0f, 1f)]
    public float heightTintStrength = 0.45f;

    [Tooltip("基础贴图采样的色彩空间：一般 Albedo 用 sRGB（保持开启）。")]
    public bool baseAlbedoIsSRGB = true;

    [Header("Physics Safety")]
    [Tooltip("高程分帧平滑写入，防止一帧突变把刚体弹飞。强烈建议开启。")]
    public bool smoothHeightUpdates = true;

    [Tooltip("每帧最大高度变化（归一化值，0.001 ≈ 0.6cm/frame @30fps，足够平滑）")]
    public float maxHeightDeltaPerFrame = 0.002f;

    bool _layersInitialized;
    Coroutine _smoothCoroutine;

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

        var td = terrain != null ? terrain.terrainData : terrainData;
        if (td == null)
        {
            Debug.LogWarning("[HandleElevationMap] 未设置 Terrain 或 TerrainData");
            return;
        }

        // Ensure TerrainData height axis covers the real elevation range.
        // This guarantees 1 Unity unit == 1 metre vertically.
        float terrainHeightAxis = td.size.y;
        if (terrainHeightAxis < range)
        {
            td.size = new Vector3(td.size.x, range * 1.2f, td.size.z);
            terrainHeightAxis = td.size.y;
        }

        // ── 第二遍：生成 heightmap + normalizedMap ──
        float[,] heights = new float[h + 1, w + 1];
        float[,] normalizedMap = new float[h, w];

        // For coloring (when not using heightmap min/max): meters-based normalization.
        float colorMin = actualMin;
        float colorRange = range;
        if (!colorFromHeightmapRange)
        {
            if (useGlobalRangeForColoring
                && msg.metadata.max_elevation > msg.metadata.min_elevation)
            {
                colorMin = msg.metadata.min_elevation;
                colorRange = msg.metadata.max_elevation - msg.metadata.min_elevation;
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int raw = msg.data[y * w + x];
                if (raw == NODATA) raw = rawMin;

                float meters = raw * hr;
                // Height uses this tile's own range -> correct terrain surface.
                float heightVal = Mathf.Clamp01((meters - actualMin) / terrainHeightAxis);

                heights[y, x] = heightVal;
                if (y == h - 1) heights[y + 1, x] = heightVal;
                if (x == w - 1) heights[y, x + 1] = heightVal;
                if (y == h - 1 && x == w - 1) heights[y + 1, x + 1] = heightVal;

                if (!colorFromHeightmapRange)
                {
                    float normalized = Mathf.Clamp01((meters - colorMin) / colorRange);
                    normalizedMap[y, x] = normalized;
                }
            }
        }

        // Map 0–1 height samples to 0–1 color index so low/high on *this* tile always span the full gradient.
        if (colorFromHeightmapRange)
        {
            float minH = float.MaxValue;
            float maxH = float.MinValue;
            int rows = h + 1;
            int cols = w + 1;
            for (int iy = 0; iy < rows; iy++)
            {
                for (int ix = 0; ix < cols; ix++)
                {
                    float v = heights[iy, ix];
                    if (v < minH) minH = v;
                    if (v > maxH) maxH = v;
                }
            }

            float span = Mathf.Max(maxH - minH, 1e-6f);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    normalizedMap[y, x] = (heights[y, x] - minH) / span;
            }
        }

        if (smoothHeightUpdates)
        {
            // Stop any previous smooth coroutine so new data always wins.
            if (_smoothCoroutine != null) StopCoroutine(_smoothCoroutine);
            _smoothCoroutine = StartCoroutine(SmoothSetHeights(td, heights, normalizedMap, w, h));
            return; // Coloring is applied inside the coroutine after heights converge.
        }

        td.SetHeights(0, 0, heights);
        NotifyTerrainHeightmapReady();

        if (enableColoring)
        {
            EnsureGradient();
            if (!_layersInitialized || rebuildTerrainLayersEveryUpdate)
            {
                InitTerrainLayers(td);
                _layersInitialized = true;
            }
            ApplyElevationColors(td, normalizedMap, w, h);

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                Debug.Log($"[HandleElevationMap] tile={name} layers={td.terrainLayers.Length} " +
                          $"alphamap={td.alphamapWidth}x{td.alphamapHeight} " +
                          $"color={elevationGradient.Evaluate(0.5f)} enableColoring={enableColoring}");
            }
        }

        Debug.Log($"[HandleElevationMap] 实际高程范围: {actualMin:F2}m ~ {actualMax:F2}m");
    }

    void NotifyTerrainHeightmapReady()
    {
        var t = terrain != null ? terrain : null;
        if (t == null) return;
        var mgr = FindFirstObjectByType<TerrainTileManager>();
        mgr?.OnTerrainHeightsApplied(t);
    }

    bool _loggedOnce;

    IEnumerator SmoothSetHeights(TerrainData td, float[,] target, float[,] normalizedMap, int dataW, int dataH)
    {
        int rows = target.GetLength(0);
        int cols = target.GetLength(1);
        var current = td.GetHeights(0, 0, cols, rows);

        while (true)
        {
            bool done = true;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    float diff = target[r, c] - current[r, c];
                    if (Mathf.Abs(diff) > maxHeightDeltaPerFrame)
                    {
                        current[r, c] += Mathf.Sign(diff) * maxHeightDeltaPerFrame;
                        done = false;
                    }
                    else
                    {
                        current[r, c] = target[r, c];
                    }
                }
            }

            td.SetHeights(0, 0, current);

            if (done) break;

            // Yield every frame so height changes are gradual and physics can react.
            yield return null;
        }

        // Apply coloring once heights have fully settled.
        if (enableColoring)
        {
            EnsureGradient();
            if (!_layersInitialized || rebuildTerrainLayersEveryUpdate)
            {
                InitTerrainLayers(td);
                _layersInitialized = true;
            }
            ApplyElevationColors(td, normalizedMap, dataW, dataH);
        }

        NotifyTerrainHeightmapReady();
        _smoothCoroutine = null;
    }

    void InitTerrainLayers(TerrainData td)
    {
        // Ensure alphamap resolution is valid. A cloned TerrainData can have 0 or too small.
        if (td.alphamapResolution < 64)
            td.alphamapResolution = 128;
        if (td.baseMapResolution < 64)
            td.baseMapResolution = 256;

        var layers = new TerrainLayer[colorBands];
        bool useBase = baseAlbedo != null;
        for (int i = 0; i < colorBands; i++)
        {
            float t = (float)i / (colorBands - 1);
            Color col = elevationGradient.Evaluate(t);

            // Create a small tiling texture: solid color + optional grid + subtle noise.
            int res = Mathf.Clamp(gridTextureResolution, 16, 256);
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var px = new Color[res * res];
            Color[] npx = generateSurfaceNormalMap ? new Color[res * res] : null;

            int every = Mathf.Max(2, gridLineEveryPixels);
            int lineW = Mathf.Clamp(gridLineWidthPixels, 1, 8);
            // Guard: if line width >= spacing, the entire texture becomes "line" (no grid visible).
            lineW = Mathf.Min(lineW, Mathf.Max(1, every - 1));
            float gridK = Mathf.Clamp01(gridLineStrength);
            float noiseK = Mathf.Clamp01(surfaceNoiseStrength);

            // Deterministic per-band seed so tiles look consistent.
            float seed = 17.123f + i * 31.7f;

            float Noise01(float x, float y)
            {
                float amp = 1f;
                float sum = 0f;
                float norm = 0f;
                float fx = x / Mathf.Max(1f, noiseFrequency);
                float fy = y / Mathf.Max(1f, noiseFrequency);
                int oct = Mathf.Clamp(noiseOctaves, 1, 4);
                for (int o = 0; o < oct; o++)
                {
                    float n = Mathf.PerlinNoise(fx + seed, fy + seed);
                    sum += n * amp;
                    norm += amp;
                    amp *= 0.5f;
                    fx *= 2f;
                    fy *= 2f;
                }
                return norm > 0f ? (sum / norm) : 0.5f;
            }

            float tintK = Mathf.Clamp01(heightTintStrength);
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    // Base: real albedo (optional) * height tint.
                    Color c;
                    if (useBase)
                    {
                        float u = (x + 0.5f) / res;
                        float v = (y + 0.5f) / res;
                        var a = baseAlbedo.GetPixelBilinear(u, v);
                        // If user provided a linear albedo texture, convert to linear-ish tinting.
                        if (!baseAlbedoIsSRGB) a = a.linear;
                        c = Color.Lerp(a, col, tintK);
                    }
                    else
                    {
                        c = col;
                    }

                    // Subtle noise to break flatness.
                    if (noiseK > 0f)
                    {
                        float n = Noise01(x, y); // 0..1
                        float m = (n - 0.5f) * 2f; // -1..1
                        // Keep noise subtle so it doesn't darken the whole surface.
                        c = Color.Lerp(c, c * (1f + 0.25f * m), noiseK);
                    }

                    // Grid overlay: brighten along lines.
                    if (overlayGridTexture && gridK > 0f)
                    {
                        bool onV = (x % every) < lineW;
                        bool onH = (y % every) < lineW;
                        if (onV || onH)
                        {
                            // Slightly brighten so it reads like engineering paint/chalk.
                            Color grid = Color.Lerp(c, Color.white, 0.35f);
                            c = Color.Lerp(c, grid, gridK);
                        }
                    }

                    // Final brightness lift (keeps colors but avoids looking muddy).
                    if (!Mathf.Approximately(surfaceBrightness, 1f))
                    {
                        c = new Color(
                            Mathf.Clamp01(c.r * surfaceBrightness),
                            Mathf.Clamp01(c.g * surfaceBrightness),
                            Mathf.Clamp01(c.b * surfaceBrightness),
                            c.a
                        );
                    }

                    px[y * res + x] = c;

                    if (npx != null)
                    {
                        float nL = Noise01(x - 1, y);
                        float nR = Noise01(x + 1, y);
                        float nD = Noise01(x, y - 1);
                        float nU = Noise01(x, y + 1);
                        float dx = (nR - nL);
                        float dy = (nU - nD);
                        Vector3 nn = new Vector3(-dx * surfaceNormalStrength, 1f, -dy * surfaceNormalStrength).normalized;
                        npx[y * res + x] = new Color(nn.x * 0.5f + 0.5f, nn.y * 0.5f + 0.5f, nn.z * 0.5f + 0.5f, 1f);
                    }
                }
            }

            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = (overlayGridTexture && crispGridLines) ? FilterMode.Point : FilterMode.Bilinear;
            tex.Apply(false, false);

            var layer = new TerrainLayer();
            layer.diffuseTexture = tex;
            if (baseNormal != null)
            {
                layer.normalMapTexture = baseNormal;
                layer.normalScale = 1f;
            }
            else if (npx != null)
            {
                var ntex = new Texture2D(res, res, TextureFormat.RGBA32, false);
                ntex.SetPixels(npx);
                ntex.wrapMode = TextureWrapMode.Repeat;
                ntex.filterMode = FilterMode.Bilinear;
                ntex.Apply(false, false);
                layer.normalMapTexture = ntex;
                layer.normalScale = 1f;
            }
            // Repeat the grid texture in world space so it reads as a dense engineering grid.
            // Smaller tileSize => more repeats across the terrain.
            float rep = Mathf.Max(0.01f, gridRepeatMeters);
            layer.tileSize = new Vector2(rep, rep);
            layer.tileOffset = Vector2.zero;
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