using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Networking;

public class OSMMapController : MonoBehaviour
{
    OSMMapElement mapElement;
    MqttManager mqtt;
    bool centerSet;
    VisualElement container;
    double minLat, minLon, maxLat, maxLon;

    void Start()
    {
        var doc = GetComponent<UIDocument>();
        if (doc == null)
            doc = FindFirstObjectByType<UIDocument>();
        container = doc != null ? doc.rootVisualElement.Q<VisualElement>("overpass-map") : null;
        mapElement = new OSMMapElement();
        if (container != null)
        {
            container.Add(mapElement);
            Debug.Log("[OSM] Map element attached");
        }
        else
        {
            Debug.LogWarning("[OSM] Container 'overpass-map' not found; map UI is disabled.");
            enabled = false;
            return;
        }

        // Build 下不要用 Application.dataPath 直接读工程文件。
        // 优先从 StreamingAssets 加载（把 map.osm 放到 Assets/StreamingAssets/map/map.osm）
        StartCoroutine(LoadOsmAndInit());

        mqtt = FindFirstObjectByType<MqttManager>();
        if (mqtt != null) mqtt.OnRtkUpdated += OnRtk;
        Invoke(nameof(LogContainerSize), 0.2f);
    }

    void OnDestroy()
    {
        if (mqtt != null) mqtt.OnRtkUpdated -= OnRtk;
    }

    void OnRtk(RtkGpsMsg rtk)
    {
        if (rtk.position != null && rtk.position.global != null)
        {
            double lat = rtk.position.global.latitude;
            double lon = rtk.position.global.longitude;
            var rot = new Quaternion(rtk.rotation.x, rtk.rotation.y, rtk.rotation.z, rtk.rotation.w);
            var vel = new Vector2(rtk.velocity.x, rtk.velocity.y);
            mapElement.Offset = Vector2.zero;
            mapElement.SetCenter(lat, lon);
            mapElement.SetRobotPose(lat, lon, rot, vel);
        }
    }

    void LogContainerSize()
    {
        if (container != null)
            Debug.Log($"[OSM] container:{container.contentRect.width:F0}x{container.contentRect.height:F0} map:{mapElement.contentRect.width:F0}x{mapElement.contentRect.height:F0}");
    }

    void FitAfterGeometry()
    {
        mapElement.FitToData(minLat, minLon, maxLat, maxLon);
        Debug.Log($"[OSM] Fit view with bbox");
    }

    System.Collections.IEnumerator LoadOsmAndInit()
    {
        string[] candidatePaths =
        {
            // Recommended for builds
            Path.Combine(Application.streamingAssetsPath, "map/map.osm"),
            // Editor-friendly fallback (project file)
            Path.Combine(Application.dataPath, "map/map.osm"),
        };

        string chosen = null;
        foreach (var p in candidatePaths)
        {
            if (string.IsNullOrEmpty(p)) continue;

            // On some platforms StreamingAssets may be inside a jar/uri; File.Exists won't work.
            if (p.Contains("://") || Application.platform == RuntimePlatform.Android)
            {
                chosen = p;
                break;
            }

            if (File.Exists(p))
            {
                chosen = p;
                break;
            }
        }

        if (string.IsNullOrEmpty(chosen))
        {
            Debug.LogError("[OSM] OSM file not found in StreamingAssets or DataPath. " +
                           "Place it at Assets/StreamingAssets/map/map.osm");
            yield break;
        }

        Debug.Log($"[OSM] Loading {chosen}");

        string localPath = chosen;
        if (chosen.Contains("://") || Application.platform == RuntimePlatform.Android)
        {
            using var req = UnityWebRequest.Get(chosen);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[OSM] Failed to load OSM via UnityWebRequest: {req.error}");
                yield break;
            }

            // Write to a readable location then parse with XmlReader
            localPath = Path.Combine(Application.persistentDataPath, "map.osm");
            try
            {
                File.WriteAllBytes(localPath, req.downloadHandler.data);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[OSM] Failed to cache OSM to persistentDataPath: {e.Message}");
                yield break;
            }
        }

        if (!File.Exists(localPath))
        {
            Debug.LogError($"[OSM] OSM file not found at {localPath}");
            yield break;
        }

        var data = OSMLoader.LoadFromFile(localPath);
        mapElement.SetMap(data);

        // 计算数据中心，作为初始视图中心
        double sumLat = 0, sumLon = 0;
        int count = 0;
        minLat = double.MaxValue; maxLat = double.MinValue; minLon = double.MaxValue; maxLon = double.MinValue;
        foreach (var n in data.nodes.Values)
        {
            sumLat += n.lat;
            sumLon += n.lon;
            count++;
            if (n.lat < minLat) minLat = n.lat;
            if (n.lat > maxLat) maxLat = n.lat;
            if (n.lon < minLon) minLon = n.lon;
            if (n.lon > maxLon) maxLon = n.lon;
        }
        if (count > 0)
            mapElement.SetCenter(sumLat / count, sumLon / count);

        Debug.Log($"[OSM] nodes:{data.nodes.Count} roads:{data.highways.Count} bounds:{data.boundaries.Count} bbox:[{minLat:F6},{minLon:F6}] - [{maxLat:F6},{maxLon:F6}]");
        mapElement.MarkDirtyRepaint();
        Invoke(nameof(FitAfterGeometry), 0.2f);
    }
}
