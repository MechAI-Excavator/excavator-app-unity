using UnityEngine;
using UnityEngine.UIElements;

public class UILogic : MonoBehaviour
{
    void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        var center = root.Q<VisualElement>("center-view");

        if (center != null)
            center.pickingMode = PickingMode.Ignore;
    }
}
