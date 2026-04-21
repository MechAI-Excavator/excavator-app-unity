using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws a position trail for the excavator (or any moving transform).
/// Attach to the same GameObject as ExcavatorController, or any parent/child.
/// Requires a LineRenderer component on the same GameObject.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class TrailDrawer : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("The transform to track. Leave empty to track this GameObject.")]
    public Transform target;

    [Header("Trail settings")]
    [Tooltip("Minimum distance (m) the target must move before a new point is recorded.")]
    public float minRecordDistance = 0.3f;

    [Tooltip("Maximum number of points kept. Oldest points are removed when exceeded.")]
    public int maxPoints = 1000;

    [Tooltip("Height offset above the recorded position (keeps trail visible on terrain).")]
    public float heightOffset = 0.15f;

    [Header("Line appearance")]
    public float lineWidth = 0.2f;
    public Color startColor = new Color(0f, 0.8f, 1f, 1f);
    public Color endColor   = new Color(0f, 0.8f, 1f, 0.2f);

    LineRenderer _lr;
    readonly List<Vector3> _points = new();
    Vector3 _lastRecorded;

    void Awake()
    {
        _lr = GetComponent<LineRenderer>();

        _lr.useWorldSpace   = true;
        _lr.positionCount   = 0;
        _lr.startWidth      = lineWidth;
        _lr.endWidth        = lineWidth * 0.3f;
        _lr.numCapVertices  = 4;
        _lr.numCornerVertices = 4;

        ApplyColors();

        // Default material that works in Built-in pipeline without extra setup.
        if (_lr.sharedMaterial == null || _lr.sharedMaterial.name == "Default-Material")
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            _lr.material = mat;
        }
    }

    void Start()
    {
        if (target == null) target = transform;
        _lastRecorded = TrailPoint(target.position);
        AddPoint(_lastRecorded);
    }

    void Update()
    {
        if (target == null) return;

        var candidate = TrailPoint(target.position);
        if (Vector3.Distance(candidate, _lastRecorded) >= minRecordDistance)
        {
            _lastRecorded = candidate;
            AddPoint(candidate);
        }

        // Keep the very last segment live so the line reaches the current position
        // even between recorded points.
        if (_points.Count >= 2)
        {
            _lr.positionCount = _points.Count + 1;
            _lr.SetPosition(_points.Count, TrailPoint(target.position));
        }
    }

    void AddPoint(Vector3 p)
    {
        _points.Add(p);

        // Trim oldest points when over limit.
        while (_points.Count > maxPoints)
            _points.RemoveAt(0);

        _lr.positionCount = _points.Count;
        _lr.SetPositions(_points.ToArray());

        ApplyColors();
    }

    Vector3 TrailPoint(Vector3 worldPos) =>
        new Vector3(worldPos.x, worldPos.y + heightOffset, worldPos.z);

    void ApplyColors()
    {
        _lr.startColor = startColor;
        _lr.endColor   = endColor;
    }

    /// <summary>Clear the trail from code or a UI button.</summary>
    public void ClearTrail()
    {
        _points.Clear();
        _lr.positionCount = 0;
        if (target != null)
        {
            _lastRecorded = TrailPoint(target.position);
            AddPoint(_lastRecorded);
        }
    }
}
