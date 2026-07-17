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
    public int colorBands = 8;

    [Tooltip("使用不受太阳、环境光和反射影响的高程色图材质。效果最接近 VertexColorUnlit。")]
    public bool useUnlitHeightMaterial = true;

    [Header("Performance")]
    [Tooltip("打开：按高度染色（建议开启）。关闭：只写高度，不做 alphamap，性能最好。")]
    public bool enableColoring = true;

    [Tooltip("是否每次都重建 TerrainLayer（非常耗时）。建议关闭，仅首次初始化。")]
    public bool rebuildTerrainLayersEveryUpdate = false;

    [Tooltip("使用 metadata.min_elevation / max_elevation 做归一化（跨 tile 颜色可比）。若全局范围远大于本 tile 实际高差，画面会几乎单色。")]
    public bool useGlobalRangeForColoring = false;

    [Tooltip("按本 tile 高度图 min/max 着色（推荐开启）：颜色与地表起伏一一对应，渐变明显。")]
    public bool colorFromHeightmapRange = true;

    [Tooltip("跳过数据、尺寸和高程比例均未变化的重复 MQTT 帧。")]
    public bool skipUnchangedElevationFrames = true;

    [Tooltip("高度变化小于该归一化值时不写入 Terrain。用于抑制无意义的浮点微小变化。")]
    [Range(0f, 0.01f)]
    public float heightChangeEpsilon = 0.00001f;

    [Header("Surface Look")]
    [Tooltip("在每个高度色带的贴图上叠加栅格线（更有工程/测绘质感）。")]
    public bool overlayGridTexture = false;

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
    public float surfaceNoiseStrength = 0f;

    [Tooltip("让栅格线更锐利（Point 采样），关闭则更平滑（Bilinear）。")]
    public bool crispGridLines = true;

    [Tooltip("栅格贴图在世界中每隔多少米重复一次（越小越密）。例如 2 表示每 2m 一组栅格纹理。")]
    [Range(0.5f, 25f)]
    public float gridRepeatMeters = 2f;

    [Tooltip("为地表自动生成法线贴图（建议开启，会显著降低“光滑假”的感觉）。")]
    public bool generateSurfaceNormalMap = false;

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
    public float surfaceBrightness = 1f;

    [Tooltip("使用 Legacy Diffuse 地形材质并关闭反射探针，彻底移除 PBR 镜面高光。")]
    public bool matteSurface = true;

    [Header("Unlit Surface Grid")]
    [Tooltip("在 Unlit 高程色图上显示横纵网格线。")]
    public bool showSurfaceGrid = true;

    [Tooltip("网格计算分辨率。picture2 使用 200；设为 0 时跟随 MQTT 数据宽高。")]
    [Min(0)]
    public int surfaceGridResolution = 200;

    [Tooltip("每隔多少个高程单元画一条线。picture2 使用 8。")]
    [Range(1, 32)]
    public int gridEveryNthCell = 8;

    [Tooltip("网格线颜色。")]
    public Color surfaceGridColor = Color.white;

    [Tooltip("网格线透明度。")]
    [Range(0f, 1f)]
    public float surfaceGridAlpha = 0.35f;

    [Tooltip("网格线屏幕宽度倍率，1 通常约为一个像素。")]
    [Range(0.25f, 3f)]
    public float surfaceGridLineWidth = 1f;

    [Header("Real material (optional)")]
    [Tooltip("基础地表 Albedo（可平铺）。设置后将用真实贴图叠加高度分色 tint，而不是纯色底。")]
    public Texture2D baseAlbedo;

    [Tooltip("基础地表 Normal（可平铺，Texture Type 必须是 Normal map）。设置后将直接复用该法线贴图。")]
    public Texture2D baseNormal;

    [Tooltip("高度分色 tint 强度：0=完全原始土贴图，1=完全用渐变色覆盖。建议 0.25~0.55。")]
    [Range(0f, 1f)]
    public float heightTintStrength = 1f;

    [Tooltip("基础贴图采样的色彩空间：一般 Albedo 用 sRGB（保持开启）。")]
    public bool baseAlbedoIsSRGB = true;

    [Header("Physics Safety")]
    [Tooltip("高程分帧平滑写入，防止一帧突变把刚体弹飞。强烈建议开启。")]
    public bool smoothHeightUpdates = true;

    [Tooltip("每帧最大高度变化（归一化值，0.001 ≈ 0.6cm/frame @30fps，足够平滑）")]
    public float maxHeightDeltaPerFrame = 0.002f;

    [Header("Data Orientation")]
    [Tooltip("后端栅格坐标方向与 Unity Terrain X/Z 相反时开启。等价于把整张高程图旋转 180 度后再写入 Terrain。")]
    public bool rotateData180 = true;

    bool _layersInitialized;
    Coroutine _smoothCoroutine;
    Material _unlitHeightMaterial;
    Texture2D _unlitHeightTexture;
    float[,] _lastTargetHeights;
    RectInt _pendingHeightRect;
    bool _hasPendingHeightRect;
    ulong _lastPayloadHash;
    bool _hasLastPayloadHash;
    float _lastColorMin;
    float _lastColorMax;
    bool _hasLastColorRange;
    float _nextInvalidFrameWarningTime;
    bool _hasRenderedValidMap;

    void Reset()
    {
        elevationGradient = CreateDefaultGradient();
    }

    void Awake()
    {
        EnsureGradient();
        ApplySurfaceRenderSettings();
    }

    void OnValidate()
    {
        heightChangeEpsilon = Mathf.Max(0f, heightChangeEpsilon);
        maxHeightDeltaPerFrame = Mathf.Max(0.000001f, maxHeightDeltaPerFrame);

        // Inspector changes to the gradient or normalization settings must force one refresh.
        _hasLastPayloadHash = false;
        _hasLastColorRange = false;
    }

    void ApplySurfaceRenderSettings()
    {
        if (terrain == null) return;

        if (matteSurface || useUnlitHeightMaterial)
            terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        if (useUnlitHeightMaterial)
        {
            EnsureUnlitHeightMaterial();
            return;
        }

        if (!matteSurface) return;

#pragma warning disable 618
        terrain.materialType = Terrain.MaterialType.BuiltInLegacyDiffuse;
#pragma warning restore 618
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
        g.mode = GradientMode.Blend;
        g.SetKeys(
            new[]
            {
                new GradientColorKey((Color)new Color32(0x57, 0x00, 0x63, 0xff), 0f / 7f),
                new GradientColorKey((Color)new Color32(0x00, 0x10, 0x48, 0xff), 1f / 7f),
                new GradientColorKey((Color)new Color32(0x00, 0x46, 0x9a, 0xff), 2f / 7f),
                new GradientColorKey((Color)new Color32(0x00, 0xa2, 0xa8, 0xff), 3f / 7f),
                new GradientColorKey((Color)new Color32(0x00, 0xff, 0x6e, 0xff), 4f / 7f),
                new GradientColorKey((Color)new Color32(0x97, 0xfa, 0x00, 0xff), 5f / 7f),
                new GradientColorKey((Color)new Color32(0xff, 0xf3, 0x00, 0xff), 6f / 7f),
                new GradientColorKey((Color)new Color32(0xff, 0x05, 0x00, 0xff), 7f / 7f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) }
        );
        return g;
    }

    /// <summary>由 MqttManager 在收到 01/map/elevation 时调用</summary>
    public void OnElevationDataReceived(ElevationMsg msg)
    {
        if (msg?.metadata == null || msg.data == null || msg.data_type != "int16") return;

        // TerrainTileManager may assign the Terrain reference after Awake.
        ApplySurfaceRenderSettings();
        if (!_hasRenderedValidMap && enableColoring && useUnlitHeightMaterial && terrain != null)
            terrain.drawHeightmap = false;

        int w = msg.metadata.width;
        int h = msg.metadata.height;
        if (w <= 0 || h <= 0) return;

        int total = w * h;
        if (msg.data.Length < total)
        {
            Debug.LogWarning(
                $"[HandleElevationMap] 高程数据长度不足: expected={total}, actual={msg.data.Length}");
            return;
        }

        int noData = msg.metadata.invalid_value;

        // First pass: min/max plus a stable payload hash. Exact repeat frames can return
        // before allocating arrays or touching TerrainData/Texture2D.
        int rawMin = int.MaxValue;
        int rawMax = int.MinValue;
        ulong payloadHash = 1469598103934665603UL;
        payloadHash = MixHash(payloadHash, w);
        payloadHash = MixHash(payloadHash, h);
        payloadHash = MixHash(payloadHash, noData);
        payloadHash = MixHash(payloadHash, msg.metadata.height_resolution.GetHashCode());
        payloadHash = MixHash(payloadHash, rotateData180 ? 1 : 0);
        payloadHash = MixHash(payloadHash, colorFromHeightmapRange ? 1 : 0);
        if (!colorFromHeightmapRange && useGlobalRangeForColoring)
        {
            payloadHash = MixHash(payloadHash, msg.metadata.min_elevation.GetHashCode());
            payloadHash = MixHash(payloadHash, msg.metadata.max_elevation.GetHashCode());
        }

        for (int i = 0; i < total; i++)
        {
            int v = msg.data[i];
            payloadHash = MixHash(payloadHash, v);
            if (v == noData) continue;
            if (v < rawMin) rawMin = v;
            if (v > rawMax) rawMax = v;
        }

        if (rawMin > rawMax)
        {
            // Never erase a good map with an all-invalid frame.
            if (Time.unscaledTime >= _nextInvalidFrameWarningTime)
            {
                _nextInvalidFrameWarningTime = Time.unscaledTime + 5f;
                Debug.LogWarning(
                    $"[HandleElevationMap] 高程帧 seq={msg.sequence} 全部为 invalid_value " +
                    $"({noData})，已保留上一张有效地图");
            }
            return;
        }

        if (skipUnchangedElevationFrames
            && _hasLastPayloadHash
            && payloadHash == _lastPayloadHash)
        {
            return;
        }

        float hr = msg.metadata.height_resolution;
        if (hr <= 0f)
        {
            Debug.LogWarning($"[HandleElevationMap] 非法 height_resolution={hr}");
            return;
        }

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

        // Second pass: build the new targets. The arrays are cheap (~80 KB for 100x100);
        // the expensive Unity writes below are restricted to the dirty rectangle.
        float[,] heights = new float[h + 1, w + 1];
        float[,] normalizedMap = new float[h, w];

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
                int raw = msg.data[GetDataIndex(x, y, w, h)];
                if (raw == noData) raw = rawMin;

                float meters = raw * hr;
                float heightVal = Mathf.Clamp01((meters - actualMin) / terrainHeightAxis);

                heights[y, x] = heightVal;
                if (y == h - 1) heights[y + 1, x] = heightVal;
                if (x == w - 1) heights[y, x + 1] = heightVal;
                if (y == h - 1 && x == w - 1) heights[y + 1, x + 1] = heightVal;

                normalizedMap[y, x] = colorFromHeightmapRange
                    ? Mathf.Clamp01((meters - actualMin) / range)
                    : Mathf.Clamp01((meters - colorMin) / colorRange);
            }
        }

        RectInt dirtyRect = FindDirtyRect(_lastTargetHeights, heights, heightChangeEpsilon);

        // A newer frame supersedes the target of the running coroutine. Preserve its pending
        // region so samples that were still converging continue toward the new target.
        StopSmoothHeightUpdate(td);
        if (_hasPendingHeightRect)
            dirtyRect = UnionRects(dirtyRect, _pendingHeightRect);

        float colorMax = colorMin + colorRange;
        bool colorRangeChanged = !_hasLastColorRange
            || !Mathf.Approximately(_lastColorMin, colorMin)
            || !Mathf.Approximately(_lastColorMax, colorMax);

        RectInt colorRect = colorRangeChanged
            ? new RectInt(0, 0, w, h)
            : ClampRectToSize(dirtyRect, w, h);

        // Color is independent of the collider smoothing. Upload it immediately so the
        // Terrain never waits several seconds in its white/default-material state.
        if (enableColoring && colorRect.width > 0 && colorRect.height > 0)
            ApplyColoring(td, normalizedMap, w, h, colorRect);

        if (terrain != null && (enableColoring || !smoothHeightUpdates))
        {
            terrain.drawHeightmap = true;
            _hasRenderedValidMap = true;
        }

        _lastColorMin = colorMin;
        _lastColorMax = colorMax;
        _hasLastColorRange = true;
        _lastPayloadHash = payloadHash;
        _hasLastPayloadHash = true;
        _lastTargetHeights = heights;

        if (!_loggedOnce && enableColoring)
        {
            _loggedOnce = true;
            Debug.Log($"[HandleElevationMap] 即时着色已启用，heightmap={td.heightmapResolution} " +
                      $"colorTexture={w}x{h}");
        }

        if (dirtyRect.width <= 0 || dirtyRect.height <= 0)
            return;

        if (smoothHeightUpdates)
        {
            _pendingHeightRect = dirtyRect;
            _hasPendingHeightRect = true;
            _smoothCoroutine = StartCoroutine(SmoothSetHeights(td, heights, dirtyRect));
            return;
        }

        td.SetHeights(
            dirtyRect.xMin,
            dirtyRect.yMin,
            ExtractPatch(heights, dirtyRect));
        _hasPendingHeightRect = false;
        _pendingHeightRect = default;
        NotifyTerrainHeightmapReady();
    }

    int GetDataIndex(int x, int y, int width, int height)
    {
        if (!rotateData180)
            return y * width + x;

        int srcX = width - 1 - x;
        int srcY = height - 1 - y;
        return srcY * width + srcX;
    }

    void NotifyTerrainHeightmapReady()
    {
        var t = terrain != null ? terrain : null;
        if (t == null) return;
        var mgr = FindFirstObjectByType<TerrainTileManager>();
        mgr?.OnTerrainHeightsApplied(t);
    }

    bool _loggedOnce;

    static ulong MixHash(ulong hash, int value)
    {
        hash ^= unchecked((uint)value);
        return hash * 1099511628211UL;
    }

    static RectInt FindDirtyRect(float[,] previous, float[,] next, float epsilon)
    {
        int rows = next.GetLength(0);
        int cols = next.GetLength(1);
        if (previous == null
            || previous.GetLength(0) != rows
            || previous.GetLength(1) != cols)
        {
            return new RectInt(0, 0, cols, rows);
        }

        int minX = cols;
        int minY = rows;
        int maxX = -1;
        int maxY = -1;
        float threshold = Mathf.Max(0f, epsilon);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (Mathf.Abs(next[y, x] - previous[y, x]) <= threshold)
                    continue;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < minX || maxY < minY
            ? default
            : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    static RectInt UnionRects(RectInt a, RectInt b)
    {
        if (a.width <= 0 || a.height <= 0) return b;
        if (b.width <= 0 || b.height <= 0) return a;

        int minX = Mathf.Min(a.xMin, b.xMin);
        int minY = Mathf.Min(a.yMin, b.yMin);
        int maxX = Mathf.Max(a.xMax, b.xMax);
        int maxY = Mathf.Max(a.yMax, b.yMax);
        return new RectInt(minX, minY, maxX - minX, maxY - minY);
    }

    static RectInt ClampRectToSize(RectInt rect, int width, int height)
    {
        int minX = Mathf.Clamp(rect.xMin, 0, width);
        int minY = Mathf.Clamp(rect.yMin, 0, height);
        int maxX = Mathf.Clamp(rect.xMax, minX, width);
        int maxY = Mathf.Clamp(rect.yMax, minY, height);
        return new RectInt(minX, minY, maxX - minX, maxY - minY);
    }

    static float[,] ExtractPatch(float[,] source, RectInt rect)
    {
        var patch = new float[rect.height, rect.width];
        for (int y = 0; y < rect.height; y++)
        {
            for (int x = 0; x < rect.width; x++)
                patch[y, x] = source[rect.yMin + y, rect.xMin + x];
        }
        return patch;
    }

    void StopSmoothHeightUpdate(TerrainData td)
    {
        if (_smoothCoroutine == null) return;

        StopCoroutine(_smoothCoroutine);
        _smoothCoroutine = null;
        FlushDelayedHeightmap(td);
    }

    void FlushDelayedHeightmap(TerrainData td)
    {
        // Sync every Terrain instance that shares this TerrainData. This is the current
        // replacement for Terrain.ApplyDelayedHeightmapModification().
        td.SyncHeightmap();
    }

    IEnumerator SmoothSetHeights(TerrainData td, float[,] target, RectInt dirtyRect)
    {
        var targetPatch = ExtractPatch(target, dirtyRect);
        var current = td.GetHeights(
            dirtyRect.xMin,
            dirtyRect.yMin,
            dirtyRect.width,
            dirtyRect.height);
        float maxStep = Mathf.Max(0.000001f, maxHeightDeltaPerFrame);

        while (true)
        {
            bool done = true;
            for (int r = 0; r < dirtyRect.height; r++)
            {
                for (int c = 0; c < dirtyRect.width; c++)
                {
                    float diff = targetPatch[r, c] - current[r, c];
                    if (Mathf.Abs(diff) > maxStep)
                    {
                        current[r, c] += Mathf.Sign(diff) * maxStep;
                        done = false;
                    }
                    else
                    {
                        current[r, c] = targetPatch[r, c];
                    }
                }
            }

            // Delay LOD/vegetation reconstruction until the patch has converged. This is the
            // main performance win versus calling SetHeights on the entire map every frame.
            td.SetHeightsDelayLOD(dirtyRect.xMin, dirtyRect.yMin, current);

            if (done) break;
            yield return null;
        }

        FlushDelayedHeightmap(td);
        NotifyTerrainHeightmapReady();
        _hasPendingHeightRect = false;
        _pendingHeightRect = default;
        _smoothCoroutine = null;
    }

    void ApplyColoring(
        TerrainData td,
        float[,] normalizedMap,
        int dataW,
        int dataH,
        RectInt dirtyRect)
    {
        EnsureGradient();

        if (useUnlitHeightMaterial
            && ApplyUnlitHeightColors(normalizedMap, dataW, dataH, dirtyRect))
            return;

        if (!_layersInitialized || rebuildTerrainLayersEveryUpdate)
        {
            InitTerrainLayers(td);
            _layersInitialized = true;
        }
        // TerrainLayer alphamaps do not share the data-grid resolution. Keep this fallback
        // path full-frame; the default unlit path above performs true partial texture uploads.
        ApplyElevationColors(td, normalizedMap, dataW, dataH);
    }

    bool ApplyUnlitHeightColors(
        float[,] normalizedMap,
        int dataW,
        int dataH,
        RectInt dirtyRect)
    {
        if (!EnsureUnlitHeightMaterial()) return false;

        int texW = Mathf.Max(2, dataW);
        int texH = Mathf.Max(2, dataH);
        bool createdTexture = false;
        if (_unlitHeightTexture == null
            || _unlitHeightTexture.width != texW
            || _unlitHeightTexture.height != texH)
        {
            if (_unlitHeightTexture != null)
                Destroy(_unlitHeightTexture);

            // Mipmaps add a full texture rebuild to every live update and are unnecessary
            // for this small engineering color map.
            _unlitHeightTexture = new Texture2D(texW, texH, TextureFormat.RGBA32, false, false)
            {
                name = $"{name}_HeightColors",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };
            createdTexture = true;
        }

        RectInt uploadRect = createdTexture
            ? new RectInt(0, 0, texW, texH)
            : ClampRectToSize(dirtyRect, texW, texH);
        if (uploadRect.width <= 0 || uploadRect.height <= 0)
            return true;

        var pixels = new Color32[uploadRect.width * uploadRect.height];
        for (int y = 0; y < uploadRect.height; y++)
        {
            int sourceY = Mathf.Clamp(uploadRect.yMin + y, 0, dataH - 1);
            for (int x = 0; x < uploadRect.width; x++)
            {
                int sourceX = Mathf.Clamp(uploadRect.xMin + x, 0, dataW - 1);
                pixels[y * uploadRect.width + x] =
                    elevationGradient.Evaluate(normalizedMap[sourceY, sourceX]);
            }
        }
        _unlitHeightTexture.SetPixels32(
            uploadRect.xMin,
            uploadRect.yMin,
            uploadRect.width,
            uploadRect.height,
            pixels);
        _unlitHeightTexture.Apply(false, false);

        _unlitHeightMaterial.SetTexture("_HeightColorMap", _unlitHeightTexture);
        ApplyUnlitGridSettings(dataW, dataH);
        return true;
    }

    bool EnsureUnlitHeightMaterial()
    {
        if (terrain == null) return false;

        var shader = Shader.Find("Custom/TerrainHeightUnlit");
        if (shader == null)
        {
            Debug.LogWarning("[HandleElevationMap] Custom/TerrainHeightUnlit shader not found; using TerrainLayers.");
            return false;
        }

        if (_unlitHeightMaterial == null || _unlitHeightMaterial.shader != shader)
        {
            if (_unlitHeightMaterial != null)
                Destroy(_unlitHeightMaterial);

            _unlitHeightMaterial = new Material(shader)
            {
                name = $"{name}_UnlitHeightMaterial",
                hideFlags = HideFlags.DontSave
            };
        }

#pragma warning disable 618
        terrain.materialType = Terrain.MaterialType.Custom;
#pragma warning restore 618
        terrain.materialTemplate = _unlitHeightMaterial;
        terrain.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        // This lightweight shader reads the Terrain mesh UV directly.
        terrain.drawInstanced = false;
        ApplyUnlitGridSettings(0, 0);
        return true;
    }

    void ApplyUnlitGridSettings(int dataW, int dataH)
    {
        if (_unlitHeightMaterial == null) return;

        int fallbackW = Mathf.Max(1, dataW);
        int fallbackH = Mathf.Max(1, dataH);
        int cellsX = surfaceGridResolution > 0 ? surfaceGridResolution : fallbackW;
        int cellsY = surfaceGridResolution > 0 ? surfaceGridResolution : fallbackH;

        _unlitHeightMaterial.SetFloat("_GridEnabled", showSurfaceGrid ? 1f : 0f);
        _unlitHeightMaterial.SetVector("_GridCells", new Vector4(cellsX, cellsY, 0f, 0f));
        _unlitHeightMaterial.SetFloat("_GridEvery", Mathf.Max(1, gridEveryNthCell));
        _unlitHeightMaterial.SetColor("_GridColor", surfaceGridColor);
        _unlitHeightMaterial.SetFloat("_GridAlpha", Mathf.Clamp01(surfaceGridAlpha));
        _unlitHeightMaterial.SetFloat("_GridLineWidth", Mathf.Max(0.01f, surfaceGridLineWidth));
    }

    void OnDestroy()
    {
        if (_unlitHeightMaterial != null)
            Destroy(_unlitHeightMaterial);
        if (_unlitHeightTexture != null)
            Destroy(_unlitHeightTexture);
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
            if (matteSurface)
            {
                layer.smoothness = 0f;
                layer.metallic = 0f;
                layer.specular = Color.black;
            }
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
