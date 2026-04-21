using UnityEngine;

public class ExcavatorController : MonoBehaviour
{
    [Header("关节引用")]
    public ArticulationBody cabin;
    public ArticulationBody boom;
    public ArticulationBody stick;
    public ArticulationBody bucket;

    [Header("速度（度/秒）")]
    public float cabinSpeed = 30f;
    public float boomSpeed = 20f;
    public float stickSpeed = 25f;
    public float bucketSpeed = 40f;

    [Header("驱动参数")]
    public float holdStiffness = 100000f;
    public float holdDamping = 10000f;
    public float moveDamping = 50000f;
    public float forceLimit = 99999999f;

    [Header("MQTT 控制")]
    [Tooltip("启用后 MQTT 角度指令优先于键盘输入")]
    public bool mqttControlEnabled = false;

    [Header("RTK 位姿")]
    [Tooltip("真实世界 1米 = Unity 多少单位（挖掘机约 10m 长，建议先用 1:1）")]
    public float worldScale = 1f;

    [Tooltip("位移插值速度，越大跟随越快")]
    public float positionLerpSpeed = 8f;

    [Tooltip("旋转插值速度，越大跟随越快")]
    public float rotationLerpSpeed = 8f;

    [Header("地面吸附")]
    [Tooltip("开启后：目标高度只从地形采样，忽略 RTK 里的高度（否则与场景地形不一致时会逐渐飘空/入地）")]
    public bool snapToGround = true;

    [Tooltip("从上方向下打射线时，起点世界 Y（足够高，避免射线起点落在地形下方导致打不中）")]
    public float raycastTopY = 500f;

    [Tooltip("挖掘机底部到根节点 pivot 的偏移（让底盘刚好贴地，而不是 pivot 贴地）")]
    public float groundOffset = 0f;

    [Tooltip("竖直方向校正增益（仅用于贴地/高度误差，不宜过大，否则像被托着飞）")]
    public float groundSnapSpeed = 6f;

    [Tooltip("水平跟随最大速度（m/s），避免像飞机平移")]
    public float maxHorizontalSpeed = 8f;

    [Tooltip("竖直方向最大速度（m/s），越小越像贴地爬行，越大越像飘")]
    public float maxVerticalSpeed = 0.6f;

    [Tooltip("最大角速度（rad/s），过大时车身会像陀螺/飘")]
    public float maxAngularVelocityRad = 2.5f;

    private float _targetCabin;
    private float _targetBoom;
    private float _targetStick;
    private float _targetBucket;

    private Vector3 _rtkTargetPos;
    private Quaternion _rtkTargetRot;
    private bool _rtkReady;

    // 挖掘机的根 ArticulationBody（base link），
    // 位姿通过 TeleportRoot 设置
    private ArticulationBody _rootBody;

    void Awake()
    {
        _rootBody = GetComponent<ArticulationBody>();
        _rtkTargetPos = transform.position;
        _rtkTargetRot = transform.rotation;
    }

    // ── 关节控制 API ─────────────────────────────────────────

    /// <summary>
    /// 由 MqttManager 调用，传入各关节相对前一关节的目标角度（度）。
    /// 调用后自动切换为 MQTT 控制模式。
    /// </summary>
    public void ApplyJointControl(float cabinAngle, float boomAngle, float stickAngle, float bucketAngle)
    {
        _targetCabin = cabinAngle;
        _targetBoom = boomAngle;
        _targetStick = stickAngle;
        _targetBucket = bucketAngle;
        mqttControlEnabled = true;
    }

    // ── RTK 位姿 API ────────────────────────────────────────

    /// <summary>
    /// 由 MqttManager 调用，传入 RTK 相对位移（米）和 ENU 四元数。
    /// RTK 坐标系 ENU: x=东, y=北, z=上
    /// Unity 坐标系:     x=右, y=上, z=前
    /// 转换: Unity.x = ENU.x, Unity.y = ENU.z, Unity.z = ENU.y
    /// </summary>
    public void ApplyRtkPose(RtkTranslation translation, RtkRotation rotation)
    {
        if (translation != null)
        {
            _rtkTargetPos = new Vector3(
                translation.x * worldScale,
                translation.z * worldScale,
                translation.y * worldScale
            );
        }

        if (rotation != null)
        {
            var enu = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            _rtkTargetRot = EnuToUnity(enu);
        }

        _rtkReady = true;
    }

    // ── FixedUpdate ─────────────────────────────────────────

    void FixedUpdate()
    {
        // 关节驱动
        if (mqttControlEnabled)
        {
            DriveToAngle(cabin, _targetCabin);
            DriveToAngle(boom, _targetBoom);
            DriveToAngle(stick, _targetStick);
            DriveToAngle(bucket, _targetBucket);
        }
        else
        {
            float cabinInput = 0f;
            if (Input.GetKey(KeyCode.A)) cabinInput = cabinSpeed;
            if (Input.GetKey(KeyCode.D)) cabinInput = -cabinSpeed;
            Drive(cabin, cabinInput);

            float boomInput = 0f;
            if (Input.GetKey(KeyCode.W)) boomInput = boomSpeed;
            if (Input.GetKey(KeyCode.S)) boomInput = -boomSpeed;
            Drive(boom, boomInput);

            float stickInput = 0f;
            if (Input.GetKey(KeyCode.UpArrow)) stickInput = -stickSpeed;
            if (Input.GetKey(KeyCode.DownArrow)) stickInput = stickSpeed;
            Drive(stick, stickInput);

            float bucketInput = 0f;
            if (Input.GetKey(KeyCode.LeftArrow)) bucketInput = -bucketSpeed;
            if (Input.GetKey(KeyCode.RightArrow)) bucketInput = bucketSpeed;
            Drive(bucket, bucketInput);
        }

        // RTK 底盘位姿
        if (_rtkReady)
            ApplyRtkPoseSmooth();
    }

    // ── RTK 位姿平滑 ────────────────────────────────────────

    private void ApplyRtkPoseSmooth()
    {
        float dt = Time.fixedDeltaTime;

        // X/Z from RTK. When snapping, ignore RTK altitude — it often drifts vs scene Terrain.
        Vector3 targetPos = _rtkTargetPos;
        if (snapToGround)
            targetPos.y = SampleGroundYAtXZ(targetPos.x, targetPos.z, transform.position.y);

        if (_rootBody != null)
        {
            // Horizontal: velocity toward RTK target (XZ only) — feels like driving, not flying.
            Vector3 posError = targetPos - transform.position;
            Vector3 horizVel = new Vector3(posError.x, 0f, posError.z) * positionLerpSpeed;
            float horizMag = horizVel.magnitude;
            if (horizMag > maxHorizontalSpeed)
                horizVel = horizVel * (maxHorizontalSpeed / horizMag);

            // Vertical: small PD toward ground height only (never use same gain as horizontal).
            float vy = Mathf.Clamp(posError.y * groundSnapSpeed, -maxVerticalSpeed, maxVerticalSpeed);
            if (Mathf.Abs(posError.y) < 0.02f)
                vy = 0f;

            _rootBody.velocity = new Vector3(horizVel.x, vy, horizVel.z);

            // Angular velocity from rotation error (clamped).
            Quaternion rotErr = _rtkTargetRot * Quaternion.Inverse(transform.rotation);
            rotErr.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (axis.sqrMagnitude > 0.001f)
            {
                Vector3 angVel = axis.normalized * (angleDeg * Mathf.Deg2Rad * rotationLerpSpeed);
                float angMag = angVel.magnitude;
                if (angMag > maxAngularVelocityRad)
                    angVel *= maxAngularVelocityRad / angMag;
                _rootBody.angularVelocity = angVel;
            }
            else
            {
                _rootBody.angularVelocity = Vector3.zero;
            }
        }
        else
        {
            Vector3 pos = Vector3.Lerp(transform.position, targetPos, positionLerpSpeed * dt);
            Quaternion rot = Quaternion.Slerp(transform.rotation, _rtkTargetRot, rotationLerpSpeed * dt);
            transform.SetPositionAndRotation(pos, rot);
        }
    }

    /// <summary>
    /// World Y of ground under (x,z). Uses a high downward ray + prefers TerrainCollider
    /// so we never pick the excavator's own colliders and never start the ray under the terrain.
    /// </summary>
    private float SampleGroundYAtXZ(float x, float z, float fallbackY)
    {
        var origin = new Vector3(x, raycastTopY, z);
        float maxDist = raycastTopY + 200f;
        var hits = Physics.RaycastAll(origin, Vector3.down, maxDist, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
            return fallbackY;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            if (h.collider is TerrainCollider)
                return h.point.y + groundOffset;
        }

        // Fallback: first hit that is not this vehicle (avoid ray hitting own mesh).
        foreach (var h in hits)
        {
            if (!h.collider.transform.IsChildOf(transform))
                return h.point.y + groundOffset;
        }

        return fallbackY;
    }

    /// <summary>
    /// ENU 四元数 → Unity 四元数。
    /// ENU(x=东,y=北,z=上) → Unity(x=右,y=上,z=前)
    /// 交换 y↔z 并翻转手性（左手系）。
    /// </summary>
    private static Quaternion EnuToUnity(Quaternion enu)
    {
        return new Quaternion(enu.x, enu.z, enu.y, -enu.w);
    }

    /// <summary>以目标位置模式驱动关节到指定角度（度）。</summary>
    void DriveToAngle(ArticulationBody body, float targetAngleDeg)
    {
        var drive = body.xDrive;
        drive.driveType = ArticulationDriveType.Target;
        drive.target = targetAngleDeg;
        drive.stiffness = holdStiffness;
        drive.damping = holdDamping;
        drive.forceLimit = forceLimit;
        body.xDrive = drive;
    }

    /// <summary>以速度模式驱动关节；速度为 0 时锁定当前位置。</summary>
    void Drive(ArticulationBody body, float velocity)
    {
        var drive = body.xDrive;
        drive.forceLimit = forceLimit;

        if (Mathf.Approximately(velocity, 0f))
        {
            drive.driveType = ArticulationDriveType.Target;
            drive.target = body.jointPosition[0] * Mathf.Rad2Deg;
            drive.stiffness = holdStiffness;
            drive.damping = holdDamping;
        }
        else
        {
            drive.driveType = ArticulationDriveType.Velocity;
            drive.targetVelocity = velocity;
            drive.stiffness = 0f;
            drive.damping = moveDamping;
        }

        body.xDrive = drive;
    }
}