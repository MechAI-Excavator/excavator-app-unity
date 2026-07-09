using UnityEngine;

public class FixedExcavatorCamera : MonoBehaviour
{
    public Transform target; // 挖掘机根节点，或驾驶室节点
    public Vector3 localOffset = new Vector3(0f, 4f, -8f);
    public Vector3 localEuler = new Vector3(20f, 0f, 0f);

    void LateUpdate()
    {
        if (target == null) return;

        transform.position = target.TransformPoint(localOffset);
        transform.rotation = target.rotation * Quaternion.Euler(localEuler);
    }
}