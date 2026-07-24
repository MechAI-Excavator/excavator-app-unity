using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class AndroidWebViewOverlay : MonoBehaviour
{
    private const string DefaultArmWebViewElementName = "arm-webview";

    [Serializable]
    public sealed class WebViewPanel
    {
        public string elementName;
        public string htmlPath;
        public bool visible = true;
        public bool skipInEditor;
    }

    [SerializeField] private WebViewPanel[] panels =
    {
        new WebViewPanel { elementName = "map-webview", htmlPath = "excavator-map/index.html", visible = true, skipInEditor = false },
        new WebViewPanel { elementName = "arm-webview", htmlPath = "2d-excavator/index.html", visible = true, skipInEditor = false },
    };

    [SerializeField] private float layoutRefreshInterval = 0.15f;
    [SerializeField] private bool transparent = true;
    [SerializeField] private bool zoom = false;
    [SerializeField] private string armWebViewElementName = "arm-webview";
    [SerializeField, Min(0f)] private float jointAnglePushInterval = 1f / 30f;

    private readonly List<RuntimeWebView> _runtimeViews = new List<RuntimeWebView>();
    private UIDocument _document;
    private Type _webViewObjectType;
    private float _nextLayoutRefresh;
    private float _nextJointAnglePush;
    private bool _warnedMissingPlugin;
    private bool _globalVisible = true;
    private bool _jointAnglesDirty;
    private bool _loggedFirstJointAnglePush;
    private bool _hasObservedJointAngles;
    private ExcavatorJointAngles _observedJointAngles;
    private ExcavatorJointAngles _pendingJointAngles;

    private sealed class RuntimeWebView
    {
        public WebViewPanel panel;
        public VisualElement element;
        public Component webView;
        public RectInt lastRect;
        public bool isLoaded;
    }

    [Serializable]
    private struct JointAnglesWebPayload
    {
        public float boom;
        public float stick;
        public float bucket;
    }

    private void Awake()
    {
        _document = GetComponent<UIDocument>();
        EnsureDefaultPanels();
    }

    private void OnEnable()
    {
        EnsureDefaultPanels();
        ExcavatorJointStateStore.Changed += OnJointAnglesChanged;
        if (ExcavatorJointStateStore.TryGetLatest(out var angles))
            OnJointAnglesChanged(angles);

        Invoke(nameof(CreateWebViews), 0.1f);
    }

    private void Update()
    {
        if (Time.unscaledTime >= _nextLayoutRefresh)
        {
            _nextLayoutRefresh = Time.unscaledTime + Mathf.Max(0.03f, layoutRefreshInterval);

            CreateWebViews();
            RefreshWebViewLayouts();
        }

        SyncLatestJointAngles();
        PushPendingJointAngles();
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(CreateWebViews));
        ExcavatorJointStateStore.Changed -= OnJointAnglesChanged;
        _hasObservedJointAngles = false;
        DestroyWebViews();
    }

    private void EnsureDefaultPanels()
    {
        if (panels != null && panels.Length > 0) return;

        panels = new[]
        {
            new WebViewPanel { elementName = "map-webview", htmlPath = "excavator-map/index.html", visible = true, skipInEditor = false },
            new WebViewPanel { elementName = "arm-webview", htmlPath = "2d-excavator/index.html", visible = true, skipInEditor = false },
        };
    }

    private void CreateWebViews()
    {
        if (_document == null || _document.rootVisualElement == null)
            return;

        _webViewObjectType = FindWebViewObjectType();
        if (_webViewObjectType == null)
        {
            if (!_warnedMissingPlugin)
            {
                Debug.LogWarning("[WebViewOverlay] unity-webview package is not loaded yet. Let Unity Package Manager resolve net.gree.unity-webview, then enter Play Mode again.");
                _warnedMissingPlugin = true;
            }
            return;
        }

        var root = _document.rootVisualElement;
        foreach (var panel in panels)
        {
            if (panel == null || string.IsNullOrEmpty(panel.elementName)) continue;
            if (HasRuntimeView(panel.elementName)) continue;
#if UNITY_EDITOR
            if (panel.skipInEditor) continue;
#endif

            var element = root.Q<VisualElement>(panel.elementName);
            if (element == null)
            {
                Debug.LogWarning($"[WebViewOverlay] UI element '{panel.elementName}' not found.");
                continue;
            }

            if (!TryGetScreenRect(element, out var rect))
                continue;

            RuntimeWebView runtimeView = null;
            try
            {
                var webViewObject = new GameObject($"WebViewOverlay-{panel.elementName}");
                webViewObject.transform.SetParent(transform, false);
                var webView = webViewObject.AddComponent(_webViewObjectType);

                runtimeView = new RuntimeWebView
                {
                    panel = panel,
                    element = element,
                    webView = webView,
                    lastRect = rect
                };

                InitWebView(webView, () => OnWebViewLoaded(runtimeView));
                SetMargins(webView, rect);
                SetVisibility(webView, panel.visible && _globalVisible);
                _runtimeViews.Add(runtimeView);

                string url = BuildLocalUrl(panel.htmlPath);
                LoadUrl(webView, url);
                Debug.Log($"[WebViewOverlay] Created '{panel.elementName}' rect={rect} url={url}");
            }
            catch (Exception ex)
            {
                if (runtimeView != null)
                {
                    _runtimeViews.Remove(runtimeView);
                    if (runtimeView.webView != null)
                        Destroy(runtimeView.webView.gameObject);
                }

                Debug.LogError($"[WebViewOverlay] Failed to create '{panel.elementName}' for '{panel.htmlPath}': {ex}");
            }
        }
    }

    private bool HasRuntimeView(string elementName)
    {
        foreach (var view in _runtimeViews)
        {
            if (view.panel != null && view.panel.elementName == elementName)
                return true;
        }
        return false;
    }

    private void RefreshWebViewLayouts()
    {
        foreach (var view in _runtimeViews)
        {
            if (view.webView == null || view.element == null) continue;
            if (!TryGetScreenRect(view.element, out var rect)) continue;

            if (!rect.Equals(view.lastRect))
            {
                view.lastRect = rect;
                SetMargins(view.webView, rect);
            }

            SetVisibility(view.webView, view.panel.visible && _globalVisible);
        }
    }

    public void SetGlobalVisibility(bool visible)
    {
        _globalVisible = visible;
        foreach (var view in _runtimeViews)
        {
            if (view.webView != null)
                SetVisibility(view.webView, view.panel.visible && _globalVisible);
        }
    }

    private bool TryGetScreenRect(VisualElement element, out RectInt rect)
    {
        rect = default;
        var root = _document.rootVisualElement;
        if (root == null) return false;

        var rootBounds = root.worldBound;
        var bounds = element.worldBound;
        if (rootBounds.width <= 0f || rootBounds.height <= 0f || bounds.width <= 0f || bounds.height <= 0f)
            return false;

        float scaleX = Screen.width / rootBounds.width;
        float scaleY = Screen.height / rootBounds.height;
        int x = Mathf.RoundToInt((bounds.x - rootBounds.x) * scaleX);
        int y = Mathf.RoundToInt((bounds.y - rootBounds.y) * scaleY);
        int width = Mathf.RoundToInt(bounds.width * scaleX);
        int height = Mathf.RoundToInt(bounds.height * scaleY);

        rect = new RectInt(x, y, width, height);
        return width > 0 && height > 0;
    }

    private void InitWebView(Component webView, Action onLoaded)
    {
        var method = _webViewObjectType.GetMethod("Init");
        if (method == null) return;

        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Type.Missing;

            switch (parameters[i].Name)
            {
                case "cb":
                    args[i] = new Action<string>(OnWebViewMessage);
                    break;
                case "err":
                    args[i] = new Action<string>((message) => Debug.LogError($"[WebViewOverlay] WebView error: {message}"));
                    break;
                case "httpErr":
                    args[i] = new Action<string>((message) => Debug.LogError($"[WebViewOverlay] WebView HTTP error: {message}"));
                    break;
                case "ld":
                    args[i] = new Action<string>((message) =>
                    {
                        Debug.Log($"[WebViewOverlay] WebView loaded: {message}");
                        onLoaded?.Invoke();
                    });
                    break;
                case "started":
                    args[i] = new Action<string>((message) => Debug.Log($"[WebViewOverlay] WebView started: {message}"));
                    break;
                case "transparent":
                    args[i] = transparent;
                    break;
                case "zoom":
                    args[i] = zoom;
                    break;
                case "enableWKWebView":
                    args[i] = true;
                    break;
                case "separated":
                    args[i] = false;
                    break;
            }
        }

        method.Invoke(webView, args);
    }

    private void OnJointAnglesChanged(ExcavatorJointAngles angles)
    {
        _observedJointAngles = angles;
        _hasObservedJointAngles = true;
        _pendingJointAngles = angles;
        _jointAnglesDirty = true;
    }

    private void SyncLatestJointAngles()
    {
        if (!ExcavatorJointStateStore.TryGetLatest(out var angles))
            return;

        if (_hasObservedJointAngles
            && angles.Timestamp.Equals(_observedJointAngles.Timestamp)
            && Mathf.Approximately(angles.Boom, _observedJointAngles.Boom)
            && Mathf.Approximately(angles.Stick, _observedJointAngles.Stick)
            && Mathf.Approximately(angles.Bucket, _observedJointAngles.Bucket))
        {
            return;
        }

        OnJointAnglesChanged(angles);
    }

    private void OnWebViewLoaded(RuntimeWebView view)
    {
        view.isLoaded = true;
        if (view.panel != null
            && IsArmWebView(view.panel)
            && ExcavatorJointStateStore.TryGetLatest(out var angles))
        {
            OnJointAnglesChanged(angles);
        }
    }

    private void PushPendingJointAngles()
    {
        if (!_jointAnglesDirty || Time.unscaledTime < _nextJointAnglePush)
            return;

        var payload = new JointAnglesWebPayload
        {
            boom = _pendingJointAngles.Boom,
            stick = _pendingJointAngles.Stick,
            bucket = _pendingJointAngles.Bucket
        };
        string json = JsonUtility.ToJson(payload);
        string script =
            "(function() { try {" +
            $"var payload = {json};" +
            "if (typeof window.applyExcavatorPayload !== 'function') {" +
            "console.error('[ExcavatorView] applyExcavatorPayload is unavailable');" +
            "return;" +
            "}" +
            "window.applyExcavatorPayload(payload);" +
            "window.__excavatorAngleAckCount = (window.__excavatorAngleAckCount || 0) + 1;" +
            "var ackNow = Date.now();" +
            "var shouldAck = window.__excavatorAngleAckCount <= 3 || " +
            "!window.__excavatorAngleLastAckAt || ackNow - window.__excavatorAngleLastAckAt >= 1000;" +
            "if (shouldAck && window.Unity && typeof window.Unity.call === 'function') {" +
            "window.__excavatorAngleLastAckAt = ackNow;" +
            "var boom = document.getElementById('boom');" +
            "var stick = document.getElementById('stick');" +
            "var bucket = document.getElementById('bucket');" +
            "var boomRect = boom ? boom.getBoundingClientRect() : null;" +
            "window.Unity.call('excavator-angles-applied|' + JSON.stringify({" +
            "seq: window.__excavatorAngleAckCount," +
            "input: payload," +
            "boomTransform: boom ? boom.style.transform : null," +
            "stickTransform: stick ? stick.style.transform : null," +
            "bucketTransform: bucket ? bucket.style.transform : null," +
            "computedBoomTransform: boom ? getComputedStyle(boom).transform : null," +
            "boomRect: boomRect ? {x: boomRect.x, y: boomRect.y, width: boomRect.width, height: boomRect.height} : null" +
            "}));" +
            "}" +
            "} catch (error) {" +
            "console.error('[ExcavatorView] failed to apply angles', error);" +
            "} })();";
        bool pushed = false;
        bool attempted = false;

        foreach (var view in _runtimeViews)
        {
            if (view.webView == null
                || !view.isLoaded
                || view.panel == null
                || !IsArmWebView(view.panel))
            {
                continue;
            }

            attempted = true;
            pushed |= TryEvaluateJavaScript(view.webView, script);
        }

        if (attempted)
        {
            _nextJointAnglePush =
                Time.unscaledTime + Mathf.Max(0f, jointAnglePushInterval);
        }

        if (!pushed)
            return;

        if (!_loggedFirstJointAnglePush)
        {
            _loggedFirstJointAnglePush = true;
            Debug.Log(
                $"[WebViewOverlay] 已向 '{DefaultArmWebViewElementName}' 推送首帧关节角度: " +
                $"boom={payload.boom:F3} stick={payload.stick:F3} " +
                $"bucket={payload.bucket:F3}");
        }

        _jointAnglesDirty = false;
    }

    private void OnWebViewMessage(string message)
    {
        const string appliedPrefix = "excavator-angles-applied|";
        if (message != null && message.StartsWith(appliedPrefix, StringComparison.Ordinal))
        {
            Debug.Log(
                "[WebViewOverlay] WebView 已实际应用关节角度: " +
                message.Substring(appliedPrefix.Length));
            return;
        }

        Debug.Log($"[WebViewOverlay] WebView message: {message}");
    }

    private bool IsArmWebView(WebViewPanel panel)
    {
        string elementName = string.IsNullOrWhiteSpace(armWebViewElementName)
            ? DefaultArmWebViewElementName
            : armWebViewElementName;
        return panel.elementName == elementName;
    }

    private bool TryEvaluateJavaScript(Component webView, string script)
    {
        try
        {
            var method = _webViewObjectType.GetMethod("EvaluateJS");
            if (method == null)
                return false;

            method.Invoke(webView, new object[] { script });
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WebViewOverlay] Failed to update excavator angles: {ex}");
            return false;
        }
    }

    private void SetMargins(Component webView, RectInt rect)
    {
        int left = Mathf.Max(0, rect.x);
        int top = Mathf.Max(0, rect.y);
        int right = Mathf.Max(0, Screen.width - rect.xMax);
        int bottom = Mathf.Max(0, Screen.height - rect.yMax);

        var method = _webViewObjectType.GetMethod("SetMargins");
        if (method == null) return;

        var parameters = method.GetParameters();
        if (parameters.Length >= 5)
            method.Invoke(webView, new object[] { left, top, right, bottom, false });
        else
            method.Invoke(webView, new object[] { left, top, right, bottom });
    }

    private void SetVisibility(Component webView, bool visible)
    {
        _webViewObjectType.GetMethod("SetVisibility")?.Invoke(webView, new object[] { visible });
    }

    private void LoadUrl(Component webView, string url)
    {
        _webViewObjectType.GetMethod("LoadURL")?.Invoke(webView, new object[] { url });
    }

    private void DestroyWebViews()
    {
        foreach (var view in _runtimeViews)
        {
            if (view.webView != null)
                Destroy(view.webView.gameObject);
        }
        _runtimeViews.Clear();
    }

    private static Type FindWebViewObjectType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("Gree.UnityWebView.WebViewObject");
            if (type != null) return type;
        }

        return null;
    }

    private static string BuildLocalUrl(string htmlPath)
    {
        string cleanPath = (htmlPath ?? string.Empty).Replace("\\", "/").TrimStart('/');
        if (cleanPath.StartsWith("WebResouces/", StringComparison.OrdinalIgnoreCase))
            cleanPath = cleanPath.Substring("WebResouces/".Length);

#if UNITY_ANDROID && !UNITY_EDITOR
        return "file:///android_asset/WebResouces/" + cleanPath;
#else
        string root = Application.isEditor
            ? Path.Combine(Application.dataPath, "WebResouces")
            : Path.Combine(Application.streamingAssetsPath, "WebResouces");
        return new Uri(Path.Combine(root, cleanPath)).AbsoluteUri;
#endif
    }
}
