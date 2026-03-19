using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class OSMMapElement : VisualElement
{
    public OSMMapData Map;
    public double CenterLat;
    public double CenterLon;
    public float Zoom = 1f;
    public Vector2 Offset;
    public Vector2? RobotLatLon;
    public float PixelsPerMeter = 1f;
    public static bool IsPointerOverMap;
    VisualElement robot;
    RobotArrowElement arrow;
    Quaternion robotRotation;
    Vector2 robotVelocity;
    bool hasPose;
    OSMMapLayerElement layerBuildings;
    OSMMapLayerElement layerWaters;
    OSMMapLayerElement layerLanduses;
    OSMMapLayerElement layerHighways;
    OSMMapLayerElement layerBoundaries;
    bool dragging;
    Vector2 dragStart;
    Vector2 offsetStart;

    public OSMMapElement()
    {
        style.flexGrow = 1;
        style.width = new Length(100, LengthUnit.Percent);
        style.height = new Length(100, LengthUnit.Percent);
        style.overflow = Overflow.Hidden;
        pickingMode = PickingMode.Position;
        generateVisualContent += OnGenerate;
        RegisterCallback<WheelEvent>(OnWheel);
        RegisterCallback<PointerDownEvent>(OnPointerDown);
        RegisterCallback<PointerMoveEvent>(OnPointerMove);
        RegisterCallback<PointerUpEvent>(OnPointerUp);
        RegisterCallback<PointerEnterEvent>(e => IsPointerOverMap = true);
        RegisterCallback<PointerLeaveEvent>(e => IsPointerOverMap = false);
        RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        robot = new VisualElement();
        robot.style.position = Position.Absolute;
        robot.style.width = 16;
        robot.style.height = 16;
        robot.style.backgroundColor = new Color(0.2f, 1f, 0.4f, 1f);
        robot.style.borderTopLeftRadius = new Length(8);
        robot.style.borderTopRightRadius = new Length(8);
        robot.style.borderBottomLeftRadius = new Length(8);
        robot.style.borderBottomRightRadius = new Length(8);
        robot.style.display = DisplayStyle.None;
        Add(robot);
        arrow = new RobotArrowElement();
        arrow.style.position = Position.Absolute;
        arrow.style.width = 60;
        arrow.style.height = 60;
        arrow.style.display = DisplayStyle.None;
        Add(arrow);
        layerBuildings = CreateLayer(OSMLayerType.Buildings);
        layerWaters = CreateLayer(OSMLayerType.Waters);
        layerLanduses = CreateLayer(OSMLayerType.Landuses);
        layerHighways = CreateLayer(OSMLayerType.Highways);
        layerBoundaries = CreateLayer(OSMLayerType.Boundaries);
        Add(layerBuildings);
        Add(layerWaters);
        Add(layerLanduses);
        Add(layerHighways);
        Add(layerBoundaries);
    }

    void OnGeometryChanged(GeometryChangedEvent e)
    {
        MarkDirtyRepaint();
        SyncLayers();
    }

    public void SetCenter(double lat, double lon)
    {
        CenterLat = lat;
        CenterLon = lon;
        MarkDirtyRepaint();
        SyncLayers();
    }

    public void SetRobot(double lat, double lon)
    {
        RobotLatLon = new Vector2((float)lat, (float)lon);
        robot.style.display = DisplayStyle.Flex;
        UpdateRobot();
    }

    public void SetRobotPose(double lat, double lon, Quaternion rotation, Vector2 velocity)
    {
        RobotLatLon = new Vector2((float)lat, (float)lon);
        robotRotation = rotation;
        robotVelocity = velocity;
        hasPose = true;
        robot.style.display = DisplayStyle.Flex;
        UpdateRobot();
    }

    void OnWheel(WheelEvent e)
    {
        float delta = e.delta.y > 0 ? -0.1f : 0.1f;
        Zoom = Mathf.Clamp(Zoom + delta, 0.3f, 8f);
        ClampOffset();
        MarkDirtyRepaint();
        SyncLayers();
        e.StopPropagation();
    }

    void OnPointerDown(PointerDownEvent e)
    {
        dragging = true;
        dragStart = e.position;
        offsetStart = Offset;
        e.StopPropagation();
    }

    void OnPointerMove(PointerMoveEvent e)
    {
        if (!dragging) return;
        var pos = (Vector2)e.position;
        var d = pos - dragStart;
        Offset = offsetStart + d;
        ClampOffset();
        MarkDirtyRepaint();
        SyncLayers();
        e.StopPropagation();
    }

    void OnPointerUp(PointerUpEvent e)
    {
        dragging = false;
        e.StopPropagation();
    }

    Vector2 Project(double lat, double lon)
    {
        float w = contentRect.width;
        float h = contentRect.height;
        double r = 6378137.0;
        double cx = r * Deg2Rad(CenterLon);
        double cy = r * Math.Log(Math.Tan(Math.PI / 4.0 + Deg2Rad(CenterLat) / 2.0));
        double x = r * Deg2Rad(lon);
        double y = r * Math.Log(Math.Tan(Math.PI / 4.0 + Deg2Rad(lat) / 2.0));
        float dx = (float)(x - cx);
        float dy = (float)(y - cy);
        float scale = PixelsPerMeter * Zoom;
        float px = w * 0.5f + Offset.x + dx * scale;
        float py = h * 0.5f + Offset.y - dy * scale;
        return new Vector2(px, py);
    }

    public void FitToData(double minLat, double minLon, double maxLat, double maxLon)
    {
        if (contentRect.width <= 0 || contentRect.height <= 0) return;
        double r = 6378137.0;
        double xMin = r * Deg2Rad(minLon);
        double xMax = r * Deg2Rad(maxLon);
        double yMin = r * Math.Log(Math.Tan(Math.PI / 4.0 + Deg2Rad(minLat) / 2.0));
        double yMax = r * Math.Log(Math.Tan(Math.PI / 4.0 + Deg2Rad(maxLat) / 2.0));
        double dx = Math.Abs(xMax - xMin);
        double dy = Math.Abs(yMax - yMin);
        if (dx <= 0 || dy <= 0) return;
        float sx = (float)(contentRect.width / dx);
        float sy = (float)(contentRect.height / dy);
        PixelsPerMeter = Mathf.Min(sx, sy) * 0.9f;
        SetCenter((minLat + maxLat) * 0.5, (minLon + maxLon) * 0.5);
        Offset = Vector2.zero;
        Zoom = 6f;
        ClampOffset();
        MarkDirtyRepaint();
        SyncLayers();
    }

    void ClampOffset()
    {
        float w = contentRect.width;
        float h = contentRect.height;
        float maxX = w * 0.45f;
        float maxY = h * 0.45f;
        Offset = new Vector2(Mathf.Clamp(Offset.x, -maxX, maxX), Mathf.Clamp(Offset.y, -maxY, maxY));
    }

    void OnGenerate(MeshGenerationContext ctx)
    {
        var painter = ctx.painter2D;
        int budget = 60000;
        painter.lineWidth = 2f;
        painter.strokeColor = Color.white;
        var cx = contentRect.width * 0.5f + Offset.x;
        var cy = contentRect.height * 0.5f + Offset.y;
        painter.fillColor = new Color(0.07f, 0.08f, 0.1f, 1f);
        painter.BeginPath();
        painter.MoveTo(new Vector2(0, 0));
        painter.LineTo(new Vector2(contentRect.width, 0));
        painter.LineTo(new Vector2(contentRect.width, contentRect.height));
        painter.LineTo(new Vector2(0, contentRect.height));
        painter.ClosePath();
        painter.Fill();
        painter.strokeColor = new Color(1f, 1f, 1f, 0.15f);
        painter.BeginPath();
        painter.MoveTo(new Vector2(0, cy));
        painter.LineTo(new Vector2(contentRect.width, cy));
        painter.MoveTo(new Vector2(cx, 0));
        painter.LineTo(new Vector2(cx, contentRect.height));
        painter.Stroke();
        painter.strokeColor = Color.white;
        if (Map != null) { }
    }



    double Deg2Rad(double d) => d * Math.PI / 180.0;

    List<Vector2> ProjectWay(OSMWay way)
    {
        var pts = new List<Vector2>(way.refs.Count);
        for (int i = 0; i < way.refs.Count; i++)
        {
            var nid = way.refs[i];
            if (!Map.nodes.TryGetValue(nid, out var node)) continue;
            pts.Add(Project(node.lat, node.lon));
        }
        return pts;
    }

    bool IsVisible(List<Vector2> pts)
    {
        if (pts.Count == 0) return false;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        float w = contentRect.width, h = contentRect.height;
        float pad = 16f;
        if (maxX < -pad || minX > w + pad) return false;
        if (maxY < -pad || minY > h + pad) return false;
        return true;
    }

    List<Vector2> Decimate(List<Vector2> pts, int maxPoints, bool ensureClosed)
    {
        if (pts.Count <= maxPoints) return pts;
        int stride = Mathf.CeilToInt((float)pts.Count / maxPoints);
        var outPts = new List<Vector2>(maxPoints + 1);
        for (int i = 0; i < pts.Count; i += stride) outPts.Add(pts[i]);
        if (ensureClosed && outPts.Count > 0)
        {
            var first = outPts[0];
            var last = outPts[outPts.Count - 1];
            if (first != last) outPts.Add(first);
        }
        return outPts;
    }

    void SyncLayers()
    {
        var layers = new[] { layerBuildings, layerWaters, layerLanduses, layerHighways, layerBoundaries };
        foreach (var l in layers)
        {
            l.CenterLat = CenterLat;
            l.CenterLon = CenterLon;
            l.Zoom = Zoom;
            l.PixelsPerMeter = PixelsPerMeter;
            l.Offset = Offset;
            l.MarkDirtyRepaint();
        }
        UpdateRobot();
    }

    public void SetMap(OSMMapData data)
    {
        Map = data;
        if (layerBuildings != null)
        {
            layerBuildings.Map = data;
            layerWaters.Map = data;
            layerLanduses.Map = data;
            layerHighways.Map = data;
            layerBoundaries.Map = data;
            SyncLayers();
        }
    }

    OSMMapLayerElement CreateLayer(OSMLayerType type)
    {
        var l = new OSMMapLayerElement();
        l.Layer = type;
        l.CenterLat = CenterLat;
        l.CenterLon = CenterLon;
        l.Zoom = Zoom;
        l.PixelsPerMeter = PixelsPerMeter;
        l.Offset = Offset;
        if (type == OSMLayerType.Highways)
        {
            l.MaxPointsPerPath = 150;
            l.MaxPaths = 400;
            l.VertexBudget = 15000;
        }
        else
        {
            l.MaxPointsPerPath = 120;
            l.MaxPaths = 250;
            l.VertexBudget = 12000;
        }
        return l;
    }

    void UpdateRobot()
    {
        if (RobotLatLon.HasValue)
        {
            var ll = RobotLatLon.Value;
            var p = Project(ll.x, ll.y);
            if (hasPose)
            {
                robot.style.display = DisplayStyle.None;
                arrow.angleRad = GetHeadingAngleRad(robotRotation);
                arrow.style.translate = new Translate(p.x - 30f, p.y - 30f, 0);
                arrow.style.display = DisplayStyle.Flex;
                arrow.MarkDirtyRepaint();
            }
            else
            {
                robot.style.translate = new Translate(p.x - 8, p.y - 8, 0);
                robot.style.display = DisplayStyle.Flex;
                arrow.style.display = DisplayStyle.None;
            }
        }
        else
        {
            robot.style.display = DisplayStyle.None;
            arrow.style.display = DisplayStyle.None;
        }
    }

    float GetHeadingAngleRad(Quaternion q)
    {
        Vector3 north = new Vector3(0, 1, 0);
        Vector3 d = q * north;
        return Mathf.Atan2(d.x, d.y);
    }

    class RobotArrowElement : VisualElement
    {
        public float angleRad = 0f;
        static readonly Color GlowOuter = new Color(0.26f, 0.52f, 0.96f, 0.12f);
        static readonly Color GlowInner = new Color(0.26f, 0.52f, 0.96f, 0.25f);
        static readonly Color ArrowFill = new Color(0.26f, 0.52f, 0.96f, 1f);
        static readonly Color ArrowBorder = new Color(1f, 1f, 1f, 0.85f);

        public RobotArrowElement()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerate;
        }

        void OnGenerate(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            float cx = contentRect.width * 0.5f;
            float cy = contentRect.height * 0.5f;

            p.fillColor = GlowOuter;
            p.BeginPath();
            p.Arc(new Vector2(cx, cy), 26f, 0f, 360f);
            p.Fill();

            p.fillColor = GlowInner;
            p.BeginPath();
            p.Arc(new Vector2(cx, cy), 16f, 0f, 360f);
            p.Fill();

            Vector2 dir = new Vector2(Mathf.Sin(angleRad), -Mathf.Cos(angleRad));
            Vector2 perp = new Vector2(dir.y, -dir.x);
            Vector2 center = new Vector2(cx, cy);

            Vector2 tip = center + dir * 18f;
            Vector2 wingL = center - dir * 10f + perp * 11f;
            Vector2 wingR = center - dir * 10f - perp * 11f;
            Vector2 notch = center - dir * 5f;

            p.fillColor = ArrowFill;
            p.BeginPath();
            p.MoveTo(tip);
            p.LineTo(wingL);
            p.LineTo(notch);
            p.LineTo(wingR);
            p.ClosePath();
            p.Fill();

            p.strokeColor = ArrowBorder;
            p.lineWidth = 1.5f;
            p.BeginPath();
            p.MoveTo(tip);
            p.LineTo(wingL);
            p.LineTo(notch);
            p.LineTo(wingR);
            p.ClosePath();
            p.Stroke();

            p.fillColor = Color.white;
            p.BeginPath();
            p.Arc(center, 3f, 0f, 360f);
            p.Fill();
        }
    }
}
