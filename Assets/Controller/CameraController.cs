using UnityEngine;
using UnityEngine.UIElements;

public class CameraController : MonoBehaviour
{
    public Transform target;
    public float distance = 5f;
    public float rotationSpeed = 0.2f;
    public float zoomSpeed = 2f;
    public bool ignoreInputOverUi = true;

    float x = 0;
    float y = 20;
    UIDocument[] _uiDocuments;

    void Start()
    {
        if (target == null)
            target = GameObject.Find("DigitalTwin_Root").transform;

        _uiDocuments = FindObjectsOfType<UIDocument>();
    }

    void LateUpdate()
    {
        if (target == null) return;

        bool pointerOverUi = ignoreInputOverUi && IsPointerOverBlockingUi();

        if (!pointerOverUi && Input.GetMouseButton(0))
        {
            x += Input.GetAxis("Mouse X") * rotationSpeed * 100;
            y -= Input.GetAxis("Mouse Y") * rotationSpeed * 100;
        }

        // 滚轮缩放
        if (!pointerOverUi)
            distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSpeed;
        distance = Mathf.Clamp(distance, 2f, 15f);

        Quaternion rotation = Quaternion.Euler(y, x, 0);
        Vector3 position = rotation * new Vector3(0, 0, -distance) + target.position;

        transform.rotation = rotation;
        transform.position = position;
    }

    bool IsPointerOverBlockingUi()
    {
        if (_uiDocuments == null || _uiDocuments.Length == 0)
            _uiDocuments = FindObjectsOfType<UIDocument>();

        if (_uiDocuments == null) return false;

        foreach (var document in _uiDocuments)
        {
            var root = document != null ? document.rootVisualElement : null;
            if (root == null || root.panel == null) continue;

            Vector2 mousePosition = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            Vector2 panelPosition = RuntimePanelUtils.ScreenToPanel(root.panel, mousePosition);
            if (IsPointerOverElement(root, panelPosition, "settings-button") ||
                IsPointerOverElement(root, panelPosition, "settings-scrim") ||
                IsPointerOverElement(root, panelPosition, "settings-drawer"))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsPointerOverElement(VisualElement root, Vector2 panelPosition, string elementName)
    {
        var element = root.Q<VisualElement>(elementName);
        if (element == null) return false;
        if (element.pickingMode == PickingMode.Ignore) return false;
        if (element.resolvedStyle.visibility != Visibility.Visible) return false;
        if (element.resolvedStyle.display == DisplayStyle.None) return false;

        return element.worldBound.Contains(panelPosition);
    }
}
