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

