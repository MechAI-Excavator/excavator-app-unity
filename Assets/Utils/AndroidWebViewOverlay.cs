using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class AndroidWebViewOverlay : MonoBehaviour
{
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

    private readonly List<RuntimeWebView> _runtimeViews = new List<RuntimeWebView>();
    private UIDocument _document;
    private Type _webViewObjectType;
    private float _nextLayoutRefresh;
    private bool _warnedMissingPlugin;
    private bool _globalVisible = true;

    private sealed class RuntimeWebView
    {
        public WebViewPanel panel;
        public VisualElement element;
        public Component webView;
        public RectInt lastRect;
    }

    private void Awake()
    {
        _document = GetComponent<UIDocument>();
        EnsureDefaultPanels();
    }

    private void OnEnable()
    {
        EnsureDefaultPanels();
        Invoke(nameof(CreateWebViews), 0.1f);
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextLayoutRefresh) return;
        _nextLayoutRefresh = Time.unscaledTime + Mathf.Max(0.03f, layoutRefreshInterval);

        CreateWebViews();
        RefreshWebViewLayouts();
    }

    private void OnDisable()
    {
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

            try
            {
                var webViewObject = new GameObject($"WebViewOverlay-{panel.elementName}");
                webViewObject.transform.SetParent(transform, false);
                var webView = webViewObject.AddComponent(_webViewObjectType);

                InitWebView(webView);
                SetMargins(webView, rect);
                SetVisibility(webView, panel.visible && _globalVisible);
                string url = BuildLocalUrl(panel.htmlPath);
                LoadUrl(webView, url);
                Debug.Log($"[WebViewOverlay] Created '{panel.elementName}' rect={rect} url={url}");

                _runtimeViews.Add(new RuntimeWebView
                {
                    panel = panel,
                    element = element,
                    webView = webView,
                    lastRect = rect
                });
            }
            catch (Exception ex)
            {
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

    private void InitWebView(Component webView)
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
                case "err":
                    args[i] = new Action<string>((message) => Debug.LogError($"[WebViewOverlay] WebView error: {message}"));
                    break;
                case "httpErr":
                    args[i] = new Action<string>((message) => Debug.LogError($"[WebViewOverlay] WebView HTTP error: {message}"));
                    break;
                case "ld":
                    args[i] = new Action<string>((message) => Debug.Log($"[WebViewOverlay] WebView loaded: {message}"));
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
