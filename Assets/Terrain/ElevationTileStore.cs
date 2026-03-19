using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple in-memory cache for elevation tiles.
/// Key: (tile_x, tile_y).
/// </summary>
public class ElevationTileStore : MonoBehaviour
{
    public struct TileKey
    {
        public int x;
        public int y;
        public TileKey(int x, int y) { this.x = x; this.y = y; }
        public override int GetHashCode() => (x * 73856093) ^ (y * 19349663);
        public override bool Equals(object obj) => obj is TileKey k && k.x == x && k.y == y;
    }

    readonly Dictionary<TileKey, ElevationMsg> _tiles = new();

    public void Put(int tileX, int tileY, ElevationMsg msg)
    {
        _tiles[new TileKey(tileX, tileY)] = msg;
    }

    public bool TryGet(int tileX, int tileY, out ElevationMsg msg)
    {
        return _tiles.TryGetValue(new TileKey(tileX, tileY), out msg);
    }
}

