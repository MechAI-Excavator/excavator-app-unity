using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maintains a fixed pool of Terrain tiles around a moving target.
/// Elevation data arrives via MQTT with metadata.tile_x / metadata.tile_y.
/// </summary>
public class TerrainTileManager : MonoBehaviour
{
    [Header("Tiling")]
    [Tooltip("Tile size in meters (Unity units if 1 unit = 1 meter). Must match backend tiling.")]
    public float tileSizeMeters = 50f;

    [Tooltip("Active radius in tiles. 1 => 3x3, 2 => 5x5.")]
    [Range(0, 4)]
    public int activeRadius = 1;

    [Header("Terrain prefab")]
    [Tooltip("Prefab with Terrain + TerrainCollider + HandleElevationMap attached. The HandleElevationMap.terrain should reference its own Terrain.")]
    public Terrain terrainPrefab;

    [Header("Follow target (optional)")]
    [Tooltip("If set, the manager will keep tiles centered around this target's position.")]
    public Transform followTarget;

    [Header("Data cache (optional)")]
    public ElevationTileStore store;

    [Header("Seam stitching")]
    [Tooltip("接缝两侧各向里混合多少格 heightmap（1=只缝边界，2~3 更平滑，利于车轮过缝）")]
    [Range(1, 4)]
    public int edgeBlendSamples = 2;

    readonly Dictionary<Vector2Int, Terrain> _active = new();
    readonly Queue<Terrain> _pool = new();

    Vector2Int _centerTile;
    bool _centerInitialized;

    void Awake()
    {
        if (store == null)
            store = FindFirstObjectByType<ElevationTileStore>();

        WarmPool();
    }

    void Update()
    {
        if (followTarget == null) return;
        var t = WorldToTile(followTarget.position);
        if (!_centerInitialized || t != _centerTile)
        {
            _centerTile = t;
            _centerInitialized = true;
            RefreshActiveSet();
        }
    }

    void WarmPool()
    {
        if (terrainPrefab == null) return;
        int need = (activeRadius * 2 + 1) * (activeRadius * 2 + 1);
        for (int i = _pool.Count; i < need; i++)
        {
            var inst = Instantiate(terrainPrefab, Vector3.zero, Quaternion.identity, transform);
            inst.gameObject.name = $"TerrainTile_{i}";
            inst.gameObject.SetActive(false);
            _pool.Enqueue(inst);
        }
    }

    Vector2Int WorldToTile(Vector3 worldPos)
    {
        int tx = Mathf.FloorToInt(worldPos.x / tileSizeMeters);
        int ty = Mathf.FloorToInt(worldPos.z / tileSizeMeters);
        return new Vector2Int(tx, ty);
    }

    Vector3 TileToWorldOrigin(Vector2Int tile)
    {
        return new Vector3(tile.x * tileSizeMeters, 0f, tile.y * tileSizeMeters);
    }

    void RefreshActiveSet()
    {
        if (terrainPrefab == null)
        {
            Debug.LogError("[TerrainTileManager] terrainPrefab is not set.");
            return;
        }

        WarmPool();

        var desired = new HashSet<Vector2Int>();
        for (int dy = -activeRadius; dy <= activeRadius; dy++)
        {
            for (int dx = -activeRadius; dx <= activeRadius; dx++)
                desired.Add(new Vector2Int(_centerTile.x + dx, _centerTile.y + dy));
        }

        // Recycle tiles not needed
        var toRemove = new List<Vector2Int>();
        foreach (var kv in _active)
            if (!desired.Contains(kv.Key))
                toRemove.Add(kv.Key);

        foreach (var key in toRemove)
        {
            var t = _active[key];
            _active.Remove(key);
            t.gameObject.SetActive(false);
            _pool.Enqueue(t);
        }

        // Allocate missing desired tiles
        foreach (var key in desired)
        {
            if (_active.ContainsKey(key)) continue;
            if (_pool.Count == 0) WarmPool();
            if (_pool.Count == 0) break;

            var t = _pool.Dequeue();
            _active[key] = t;
            t.transform.position = TileToWorldOrigin(key);
            t.gameObject.name = $"TerrainTile_{key.x}_{key.y}";
            t.gameObject.SetActive(true);

            // Optionally apply cached data immediately
            if (store != null && store.TryGet(key.x, key.y, out var cached) && cached != null)
                ApplyToTerrain(t, cached);
        }
    }

    public void OnElevationTile(ElevationMsg msg)
    {
        if (msg?.metadata == null) return;

        // Use message-provided tiling values if present
        if (msg.metadata.tile_size_meters > 0.1f)
            tileSizeMeters = msg.metadata.tile_size_meters;

        var key = new Vector2Int(msg.metadata.tile_x, msg.metadata.tile_y);

        store?.Put(key.x, key.y, msg);

        // Ensure tile is active (center around it if we don't have a followTarget)
        if (followTarget == null)
        {
            if (!_centerInitialized || key != _centerTile)
            {
                _centerTile = key;
                _centerInitialized = true;
                RefreshActiveSet();
            }
        }

        if (_active.TryGetValue(key, out var terrain) && terrain != null)
            ApplyToTerrain(terrain, msg);
    }

    /// <summary>
    /// Call after a tile's heightmap has been fully updated (sync or end of smooth apply).
    /// Averages shared edge heights with active neighbors so TerrainCollider has no step/gap at seams.
    /// </summary>
    public void OnTerrainHeightsApplied(Terrain terrain)
    {
        if (terrain == null) return;
        foreach (var kv in _active)
        {
            if (kv.Value != terrain) continue;
            // Re-stitch every active tile pair so west/south edges update when any neighbor refreshes.
            StitchAllActiveTileEdges();
            RebuildNeighbors();
            return;
        }
    }

    /// <summary>Runs seam blend for all tiles in the pool (e.g. 3×3). Cheap for ≤25 tiles.</summary>
    void StitchAllActiveTileEdges()
    {
        foreach (var key in _active.Keys)
            StitchEdgesAround(key);
    }

    void StitchEdgesAround(Vector2Int key)
    {
        // All four sides: any tile update must align west/south neighbors too (not only east/north).
        TryStitchEastWest(key, new Vector2Int(key.x + 1, key.y));
        TryStitchEastWest(new Vector2Int(key.x - 1, key.y), key);
        TryStitchNorthSouth(key, new Vector2Int(key.x, key.y + 1));
        TryStitchNorthSouth(new Vector2Int(key.x, key.y - 1), key);
    }

    void TryStitchEastWest(Vector2Int westKey, Vector2Int eastKey)
    {
        if (!_active.TryGetValue(westKey, out var west) || west == null || west.terrainData == null) return;
        if (!_active.TryGetValue(eastKey, out var east) || east == null || east.terrainData == null) return;
        StitchEastWestEdges(west, east);
    }

    void TryStitchNorthSouth(Vector2Int southKey, Vector2Int northKey)
    {
        if (!_active.TryGetValue(southKey, out var south) || south == null || south.terrainData == null) return;
        if (!_active.TryGetValue(northKey, out var north) || north == null || north.terrainData == null) return;
        StitchNorthSouthEdges(south, north);
    }

    /// <summary>West tile's +X edge meets East tile's -X edge (same world Z samples).</summary>
    void StitchEastWestEdges(Terrain west, Terrain east)
    {
        var wd = west.terrainData;
        var ed = east.terrainData;
        int rw = wd.heightmapResolution;
        int re = ed.heightmapResolution;
        if (rw != re || rw < 2) return;

        var hw = wd.GetHeights(0, 0, rw, rw);
        var he = ed.GetHeights(0, 0, re, re);
        int last = rw - 1;
        int blend = Mathf.Clamp(edgeBlendSamples, 1, Mathf.Max(1, last));

        for (int iz = 0; iz < rw; iz++)
        {
            float v = (hw[iz, last] + he[iz, 0]) * 0.5f;
            hw[iz, last] = v;
            he[iz, 0] = v;
        }

        for (int b = 1; b < blend; b++)
        {
            int wi = last - b;
            int ei = b;
            if (wi < 0 || ei > last) break;
            for (int iz = 0; iz < rw; iz++)
            {
                hw[iz, wi] = Mathf.Lerp(hw[iz, wi], hw[iz, wi + 1], 0.5f);
                he[iz, ei] = Mathf.Lerp(he[iz, ei], he[iz, ei - 1], 0.5f);
            }
        }

        wd.SetHeights(0, 0, hw);
        ed.SetHeights(0, 0, he);
    }

    /// <summary>South tile's +Z edge meets North tile's -Z edge (same world X samples).</summary>
    void StitchNorthSouthEdges(Terrain south, Terrain north)
    {
        var sd = south.terrainData;
        var nd = north.terrainData;
        int rs = sd.heightmapResolution;
        int rn = nd.heightmapResolution;
        if (rs != rn || rs < 2) return;

        var hs = sd.GetHeights(0, 0, rs, rs);
        var hn = nd.GetHeights(0, 0, rn, rn);
        int last = rs - 1;
        int blend = Mathf.Clamp(edgeBlendSamples, 1, Mathf.Max(1, last));

        for (int ix = 0; ix < rs; ix++)
        {
            float v = (hs[last, ix] + hn[0, ix]) * 0.5f;
            hs[last, ix] = v;
            hn[0, ix] = v;
        }

        for (int b = 1; b < blend; b++)
        {
            int si = last - b;
            int ni = b;
            if (si < 0 || ni > last) break;
            for (int ix = 0; ix < rs; ix++)
            {
                hs[si, ix] = Mathf.Lerp(hs[si, ix], hs[si + 1, ix], 0.5f);
                hn[ni, ix] = Mathf.Lerp(hn[ni, ix], hn[ni - 1, ix], 0.5f);
            }
        }

        sd.SetHeights(0, 0, hs);
        nd.SetHeights(0, 0, hn);
    }

    void RebuildNeighbors()
    {
        foreach (var kv in _active)
        {
            var key = kv.Key;
            var terrain = kv.Value;
            if (terrain == null) continue;

            _active.TryGetValue(new Vector2Int(key.x - 1, key.y), out var left);
            _active.TryGetValue(new Vector2Int(key.x + 1, key.y), out var right);
            _active.TryGetValue(new Vector2Int(key.x, key.y + 1), out var top);
            _active.TryGetValue(new Vector2Int(key.x, key.y - 1), out var bottom);

            terrain.SetNeighbors(left, top, right, bottom);
        }
    }

    static void ApplyToTerrain(Terrain terrain, ElevationMsg msg)
    {
        // Ensure physical tile size matches placement spacing
        if (terrain.terrainData != null && msg?.metadata != null)
        {
            float size = msg.metadata.tile_size_meters > 0.1f ? msg.metadata.tile_size_meters : 0f;
            if (size > 0.1f)
            {
                var s = terrain.terrainData.size;
                if (!Mathf.Approximately(s.x, size) || !Mathf.Approximately(s.z, size))
                    terrain.terrainData.size = new Vector3(size, s.y, size);
            }
        }

        var applier = terrain.GetComponent<HandleElevationMap>();
        if (applier == null)
        {
            Debug.LogWarning("[TerrainTileManager] Terrain tile has no HandleElevationMap component.");
            return;
        }
        applier.terrain = terrain;
        applier.terrainData = null;
        applier.OnElevationDataReceived(msg);
    }
}

