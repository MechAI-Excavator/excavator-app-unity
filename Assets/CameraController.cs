using UnityEngine;

public class CameraController : MonoBehaviour
{
    public Transform target;
    public float distance = 5f;
    public float rotationSpeed = 0.2f;
    public float zoomSpeed = 2f;

    float x = 0;
    float y = 20;

    void Start()
    {
        if (target == null)
            target = GameObject.Find("DigitalTwin_Root").transform;
    }

    void LateUpdate()
    {
        // 鼠标旋转（PC测试）
        if (Input.GetMouseButton(0))
        {
            x += Input.GetAxis("Mouse X") * rotationSpeed * 100;
            y -= Input.GetAxis("Mouse Y") * rotationSpeed * 100;
        }

        // 滚轮缩放
        distance -= Input.GetAxis("Mouse ScrollWheel") * zoomSpeed;
        distance = Mathf.Clamp(distance, 2f, 15f);

        Quaternion rotation = Quaternion.Euler(y, x, 0);
        Vector3 position = rotation * new Vector3(0, 0, -distance) + target.position;

        transform.rotation = rotation;
        transform.position = position;
    }
}
