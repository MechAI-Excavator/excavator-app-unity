using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class JointAngleCalibration
{
    [Tooltip("后端角度到 Unity 关节角度的方向。方向相反时设为 -1。")]
    public float scale = 1f;

    [Tooltip("后端零度对应到模型零位的偏移（度）。")]
    public float offsetDegrees = 0f;

    public float ToUnityDegrees(float sourceDegrees)
    {
        return sourceDegrees * scale + offsetDegrees;
    }
}

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

    [Header("01/joints 正运动学")]
    [Tooltip("收到 01/joints 后，用相对父关节角度驱动 boom/stick/bucket，并可用 rotate 控制整机全局朝向。cabin 暂不由该 topic 控制。")]
    public bool jointsTopicControlEnabled = true;

    public JointAngleCalibration boomJointCalibration = new();
    public JointAngleCalibration stickJointCalibration = new();
    public JointAngleCalibration bucketJointCalibration = new();

    [Header("01/joints 整机朝向")]
    [Tooltip("使用 joints.rotate.angle 驱动整台挖掘机的世界 Y 轴朝向，而不是 cabin 局部关节。")]
    public bool jointsRotateControlEnabled = true;

    [Tooltip("输入约定：0°=北(+Z)，90°=东(+X)，顺时针为正。模型朝向或正负方向不一致时调整 scale/offsetDegrees。")]
    public JointAngleCalibration rotateHeadingCalibration = new();

    [Min(0f)]
    [Tooltip("rotate 收到后覆盖 RTK 朝向的时长（秒）。持续以 5Hz 接收时 rotate 始终优先；设为 0 表示永久优先。")]
    public float jointsRotateRtkOverrideSeconds = 1f;

    [Tooltip("每隔一段时间打印 MQTT 原始角度、映射目标和关节实际角度。仅用于现场诊断。")]
    public bool logJointsDebug = false;

    [Min(0.1f)]
    public float jointsDebugLogInterval = 1f;

    [Header("RTK 位姿")]
    [Tooltip("真实世界 1米 = Unity 多少单位（挖掘机约 10m 长，建议先用 1:1）")]
    public float worldScale = 1f;

    [Tooltip("位移插值速度，越大跟随越快")]
    public float positionLerpSpeed = 8f;

    [Tooltip("旋转插值速度，越大跟随越快")]
    public float rotationLerpSpeed = 8f;

    [Tooltip("仅当 origin 也使用后端栅格的反向轴时开启。global/ENU origin 应保持关闭。")]
    public bool rotateElevationOriginWithHeightmap = false;

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

    [Tooltip("RTK 只控制水平朝向（Yaw），允许底盘随坡面自然俯仰/侧倾。履带车辆建议开启。")]
    public bool rtkControlsYawOnly = true;

    [Tooltip("第一次收到高程 origin 时直接放置到目标地面，避免从场景原点高速拖行几十米导致翻车。")]
    public bool teleportOnFirstPositionTarget = true;

    [Header("动态地形稳定")]
    [Tooltip("用受控的不可移动根底盘承载关节。运行时 TerrainCollider 变形不再能把整机掀翻。")]
    public bool stabilizeBaseAgainstTerrainUpdates = true;

    [Tooltip("稳定模式下底盘保持直立，RTK 只更新 Yaw；不让高程图的局部噪声改变俯仰/侧倾。")]
    public bool keepStabilizedBaseUpright = true;

    [Tooltip("履带接地范围的半宽(X)/半长(Z)，取范围内最高地面，避免坡面穿进履带。")]
    public Vector2 stabilizedFootprintHalfExtents = new Vector2(0.75f, 1.1f);

    [Tooltip("稳定模式下履带与地面的小间隙（米）。")]
    public float stabilizedGroundClearance = 0.03f;

    [Tooltip("动态高程改变时，底盘高度的跟随速度。")]
    public float stabilizedHeightLerpSpeed = 5f;

    [Header("铲臂与地面碰撞")]
    [Tooltip("忽略 boom/stick/bucket 的 Collider 与 TerrainCollider 的物理接触。" +
             "关节仍按 MQTT 运动，但碰到地形不会产生反弹或把冲量传回整车。")]
    public bool ignoreArmTerrainCollisions = true;

    private float _targetBoomAngle;
    private float _targetStickAngle;
    private float _targetBucketAngle;
    private float _commandedBoomAngle;
    private float _commandedStickAngle;
    private float _commandedBucketAngle;
    private bool _jointTargetsReady;
    private float _nextJointsDebugLogTime;
    private bool _cabinLockedForJoints;

    private Vector3 _targetBasePosition;
    private Quaternion _targetBaseRotation;
    private bool _positionTargetReady;
    private bool _rotationTargetReady;
    private bool _initialPositionTeleportPending;
    private Quaternion _uprightBaseRotation;
    private bool _jointsHeadingTargetReady;
    private float _lastJointsHeadingTime;

    // 挖掘机的根 ArticulationBody（base link），
    // 位姿通过 TeleportRoot 设置
    private ArticulationBody _rootBody;
    private readonly HashSet<Collider> _armColliders = new();
    private bool _armTerrainPolicyLogged;

    void Awake()
    {
        DisableLegacyUrdfControllerForJointsMode();
        ConfigureJointsForJointsMode();

        _rootBody = GetComponent<ArticulationBody>();
        _targetBasePosition = transform.position;
        _targetBaseRotation = transform.rotation;
        _uprightBaseRotation = YawOnly(transform.rotation);

        if (_rootBody != null && stabilizeBaseAgainstTerrainUpdates)
        {
            // TerrainData.SetHeights updates TerrainCollider at runtime. A free dynamic
            // articulation root receives those changes as contact impulses and can roll
            // over even for centimetre-level terrain updates. Keep only the root fixed;
            // child articulation drives remain fully functional.
            _rootBody.immovable = true;
            _rootBody.velocity = Vector3.zero;
            _rootBody.angularVelocity = Vector3.zero;
        }
    }

    void Start()
    {
        // TerrainTileManager creates its pooled Terrain objects during Awake. Configure the
        // active collider pairs in Start so script execution order cannot leave the first tile
        // physically colliding with the arm.
        RefreshArmTerrainCollisionPolicy();
    }

    /// <summary>
    /// Applies the arm/terrain collision policy to all currently active TerrainColliders.
    /// TerrainTileManager calls this again whenever a pooled tile is reactivated because
    /// Physics.IgnoreCollision is a runtime collider-pair setting.
    /// </summary>
    public void RefreshArmTerrainCollisionPolicy()
    {
        _armColliders.Clear();
        CollectLinkColliders(boom, _armColliders);
        CollectLinkColliders(stick, _armColliders);
        CollectLinkColliders(bucket, _armColliders);

        var terrainColliders = FindObjectsByType<TerrainCollider>(FindObjectsSortMode.None);
        int configuredPairs = 0;
        foreach (var terrainCollider in terrainColliders)
        {
            if (terrainCollider == null
                || !terrainCollider.enabled
                || !terrainCollider.gameObject.activeInHierarchy)
                continue;

            foreach (var armCollider in _armColliders)
            {
                if (armCollider == null
                    || !armCollider.enabled
                    || !armCollider.gameObject.activeInHierarchy)
                    continue;

                Physics.IgnoreCollision(
                    armCollider,
                    terrainCollider,
                    ignoreArmTerrainCollisions);
                configuredPairs++;
            }
        }

        if (!_armTerrainPolicyLogged && configuredPairs > 0)
        {
            _armTerrainPolicyLogged = true;
            string message =
                $"[Excavator] 铲臂/地形碰撞策略：" +
                $"ignore={ignoreArmTerrainCollisions}, armColliders={_armColliders.Count}, " +
                $"terrainColliders={terrainColliders.Length}, pairs={configuredPairs}";
            Debug.Log(message);
        }
    }

    private static void CollectLinkColliders(
        ArticulationBody link,
        HashSet<Collider> destination)
    {
        if (link == null) return;

        foreach (var linkCollider in link.GetComponentsInChildren<Collider>(true))
        {
            if (linkCollider != null && !(linkCollider is TerrainCollider))
                destination.Add(linkCollider);
        }
    }

    private void DisableLegacyUrdfControllerForJointsMode()
    {
        if (!jointsTopicControlEnabled) return;

        // The URDF Importer sample Controller adds a JointControl to every articulation
        // and also adds FKRobot during Start(). That controller competes with this component's
        // MQTT drives, changes their parameters, and can make the free cabin joint rotate once
        // on startup. This component is the single joint owner while 01/joints mode is active.
        var legacyController = GetComponentInParent<
            Unity.Robotics.UrdfImporter.Control.Controller>(true);
        if (legacyController != null)
            legacyController.enabled = false;
    }

    private void ConfigureJointsForJointsMode()
    {
        _cabinLockedForJoints = jointsTopicControlEnabled
            && cabin != null;

        if (!jointsTopicControlEnabled) return;

        // The new topic owns exactly boom/stick/bucket. Once the legacy URDF controller is
        // disabled, every other revolute articulation would otherwise be left with zero
        // stiffness/damping and can swing freely under gravity. That is why bulldozer_Link,
        // rev_Link (the boom's intermediate parent), pedals and cabin appeared to rotate even
        // though they were not present in the MQTT payload.
        foreach (var body in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body == null
                || body.transform == transform
                || body.jointType != ArticulationJointType.RevoluteJoint)
                continue;

            bool controlledByJointsTopic = body == boom || body == stick || body == bucket;
            body.twistLock = controlledByJointsTopic
                ? ArticulationDofLock.LimitedMotion
                : ArticulationDofLock.LockedMotion;

            if (!controlledByJointsTopic)
            {
                // Never feed the current angle back as a target for an unused joint. Some
                // imported joints expose the zero pose as a wrapped 2π value, which can make
                // a target drive visibly perform a full turn before settling.
                var drive = body.xDrive;
                drive.targetVelocity = 0f;
                body.xDrive = drive;
            }
        }
    }

    // ── 关节正运动学 API ─────────────────────────────────────

    /// <summary>
    /// Applies absolute joint angles relative to each parent link. The articulation hierarchy
    /// performs the forward-kinematics propagation from boom to stick to bucket.
    /// </summary>
    public void ApplyJointAngles(float boomAngle, float stickAngle, float bucketAngle)
    {
        if (!IsFinite(boomAngle) || !IsFinite(stickAngle) || !IsFinite(bucketAngle))
        {
            Debug.LogWarning("[Excavator] 忽略包含 NaN/Infinity 的 01/joints 消息");
            return;
        }

        _targetBoomAngle = ClampToJointLimits(
            boom,
            CalibrateJointAngle(boomJointCalibration, boomAngle));
        _targetStickAngle = ClampToJointLimits(
            stick,
            CalibrateJointAngle(stickJointCalibration, stickAngle));
        _targetBucketAngle = ClampToJointLimits(
            bucket,
            CalibrateJointAngle(bucketJointCalibration, bucketAngle));

        if (!_jointTargetsReady)
        {
            // Start at the current pose so the first MQTT frame cannot snap the arm instantly.
            _commandedBoomAngle = CurrentJointAngleDegrees(boom);
            _commandedStickAngle = CurrentJointAngleDegrees(stick);
            _commandedBucketAngle = CurrentJointAngleDegrees(bucket);
            _jointTargetsReady = true;
        }

        if (logJointsDebug && Time.unscaledTime >= _nextJointsDebugLogTime)
        {
            _nextJointsDebugLogTime = Time.unscaledTime + Mathf.Max(0.1f, jointsDebugLogInterval);
            Debug.Log(
                $"[Excavator] 01/joints raw=({boomAngle:F1},{stickAngle:F1},{bucketAngle:F1}) " +
                $"target=({_targetBoomAngle:F1},{_targetStickAngle:F1},{_targetBucketAngle:F1}) " +
                $"commanded=({_commandedBoomAngle:F1},{_commandedStickAngle:F1}," +
                $"{_commandedBucketAngle:F1}) actual=({CurrentJointAngleDegrees(boom):F1}," +
                $"{CurrentJointAngleDegrees(stick):F1},{CurrentJointAngleDegrees(bucket):F1}) " +
                $"baseEuler={FormatEuler(transform.eulerAngles)} " +
                $"uncontrolled=[{BuildUncontrolledJointDebug()}]");
        }
    }

    /// <summary>
    /// Applies the whole machine's absolute map heading. Unity's +Z is north and +X is east,
    /// so a positive Y Euler angle already matches the input convention (clockwise from above:
    /// 0° north, 90° east). The existing base-pose smoothing takes the shortest path across
    /// the 0°/360° boundary.
    /// </summary>
    public void ApplyGlobalHeading(float headingDegrees)
    {
        if (!jointsRotateControlEnabled)
            return;

        if (!IsFinite(headingDegrees))
        {
            Debug.LogWarning("[Excavator] 忽略包含 NaN/Infinity 的 joints.rotate.angle");
            return;
        }

        float unityHeading = CalibrateJointAngle(
            rotateHeadingCalibration,
            headingDegrees);
        unityHeading = Mathf.Repeat(unityHeading, 360f);

        _targetBaseRotation = Quaternion.Euler(0f, unityHeading, 0f);
        _rotationTargetReady = true;
        _jointsHeadingTargetReady = true;
        _lastJointsHeadingTime = Time.unscaledTime;
    }

    // ── RTK 位姿 API ────────────────────────────────────────

    /// <summary>
    /// 兼容旧调用：传入 RTK 相对位移（米）和 ENU 四元数。
    /// RTK 坐标系 ENU: x=东, y=北, z=上
    /// Unity 坐标系:     x=右, y=上, z=前
    /// 转换: Unity.x = ENU.x, Unity.y = ENU.z, Unity.z = ENU.y
    /// </summary>
    public void ApplyRtkPose(RtkTranslation translation, RtkRotation rotation)
    {
        if (translation != null)
        {
            _targetBasePosition = new Vector3(
                translation.x * worldScale,
                translation.z * worldScale,
                translation.y * worldScale
            );
            _positionTargetReady = true;
        }

        if (rotation != null && !JointsHeadingOverridesRtk())
        {
            var enu = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            _targetBaseRotation = EnuToUnity(enu);
            _rotationTargetReady = true;
        }
    }

    /// <summary>
    /// Updates only the base rotation from RTK. Position is supplied by elevation metadata.origin.
    /// </summary>
    public void ApplyRtkRotation(RtkRotation rotation)
    {
        if (rotation == null || JointsHeadingOverridesRtk()) return;

        var enu = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        _targetBaseRotation = EnuToUnity(enu);
        _rotationTargetReady = true;
    }

    private bool JointsHeadingOverridesRtk()
    {
        if (!jointsRotateControlEnabled || !_jointsHeadingTargetReady)
            return false;

        return jointsRotateRtkOverrideSeconds <= 0f
            || Time.unscaledTime - _lastJointsHeadingTime <= jointsRotateRtkOverrideSeconds;
    }

    /// <summary>
    /// Places the excavator at an ENU offset, in metres, from the elevation tile center.
    /// ENU x/y/z maps to Unity x/z/y. The planar offset follows the heightmap's 180-degree
    /// orientation correction when that correction is enabled on the tile.
    /// </summary>
    public void ApplyElevationOrigin(
        ElevationOrigin origin,
        Terrain referenceTerrain,
        string coordinateSystem = null)
    {
        if (origin == null || referenceTerrain == null || referenceTerrain.terrainData == null)
            return;

        float offsetX = origin.x;
        float offsetZ = origin.y;

        var elevationMap = referenceTerrain.GetComponent<HandleElevationMap>();
        bool isGlobalCoordinates = string.Equals(
            coordinateSystem,
            "global",
            System.StringComparison.OrdinalIgnoreCase);
        if (!isGlobalCoordinates
            && rotateElevationOriginWithHeightmap
            && elevationMap != null
            && elevationMap.rotateData180)
        {
            offsetX = -offsetX;
            offsetZ = -offsetZ;
        }

        var terrainSize = referenceTerrain.terrainData.size;
        Vector3 tileCenter = referenceTerrain.GetPosition()
            + new Vector3(terrainSize.x * 0.5f, 0f, terrainSize.z * 0.5f);
        Vector3 worldOffset = new Vector3(offsetX, origin.z, offsetZ) * worldScale;

        bool isFirstPositionTarget = !_positionTargetReady;
        _targetBasePosition = tileCenter + worldOffset;
        _positionTargetReady = true;
        if (isFirstPositionTarget && teleportOnFirstPositionTarget)
            _initialPositionTeleportPending = true;

        Debug.Log($"[Excavator] origin=({origin.x:F2},{origin.y:F2},{origin.z:F2})m " +
                  $"terrainCenter=({tileCenter.x:F2},{tileCenter.z:F2}) " +
                  $"target=({_targetBasePosition.x:F2},{_targetBasePosition.z:F2}) " +
                  $"physicsRoot={name} container={transform.parent?.name}");
    }

    // ── FixedUpdate ─────────────────────────────────────────

    void FixedUpdate()
    {
        // 关节驱动
        if (!jointsTopicControlEnabled && !_cabinLockedForJoints)
        {
            float cabinInput = 0f;
            if (Input.GetKey(KeyCode.A)) cabinInput = cabinSpeed;
            if (Input.GetKey(KeyCode.D)) cabinInput = -cabinSpeed;
            Drive(cabin, cabinInput);
        }

        if (jointsTopicControlEnabled && _jointTargetsReady)
        {
            UpdateJointAngleTargets();
        }
        else
        {
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

        // 高程图 origin 控制位置，RTK 控制朝向。
        if (_positionTargetReady || _rotationTargetReady)
            ApplyBasePoseSmooth();
    }

    // ── 底盘位姿平滑 ────────────────────────────────────────

    private void ApplyBasePoseSmooth()
    {
        float dt = Time.fixedDeltaTime;

        if (_rootBody != null)
        {
            if (stabilizeBaseAgainstTerrainUpdates)
            {
                ApplyStabilizedBasePose(dt);
                return;
            }

            if (_positionTargetReady)
            {
                // X/Z comes from elevation metadata.origin. Ground snapping owns final height.
                Vector3 targetPos = _targetBasePosition;
                if (snapToGround)
                    targetPos.y = SampleGroundYAtXZ(targetPos.x, targetPos.z, transform.position.y);

                if (_initialPositionTeleportPending)
                {
                    Quaternion initialRotation = _rotationTargetReady
                        ? _targetBaseRotation
                        : transform.rotation;
                    _rootBody.TeleportRoot(targetPos, initialRotation);
                    _rootBody.velocity = Vector3.zero;
                    _rootBody.angularVelocity = Vector3.zero;
                    _initialPositionTeleportPending = false;
                    return;
                }

                Vector3 posError = targetPos - transform.position;
                Vector3 horizVel = new Vector3(posError.x, 0f, posError.z) * positionLerpSpeed;
                float horizMag = horizVel.magnitude;
                if (horizMag > maxHorizontalSpeed)
                    horizVel = horizVel * (maxHorizontalSpeed / horizMag);

                float vy = Mathf.Clamp(posError.y * groundSnapSpeed, -maxVerticalSpeed, maxVerticalSpeed);
                if (Mathf.Abs(posError.y) < 0.02f)
                    vy = 0f;

                _rootBody.velocity = new Vector3(horizVel.x, vy, horizVel.z);
            }

            // Elevation position updates must not implicitly lock pitch/roll. Only an
            // actual RTK rotation message enables orientation control.
            if (_rotationTargetReady)
            {
                if (rtkControlsYawOnly)
                    ApplyYawAngularVelocity();
                else
                    ApplyFullAngularVelocity();
            }
        }
        else
        {
            Vector3 pos = transform.position;
            if (_positionTargetReady)
            {
                Vector3 targetPos = _targetBasePosition;
                if (snapToGround)
                    targetPos.y = SampleGroundYAtXZ(targetPos.x, targetPos.z, transform.position.y);
                pos = Vector3.Lerp(transform.position, targetPos, positionLerpSpeed * dt);
            }

            Quaternion rot = _rotationTargetReady
                ? Quaternion.Slerp(transform.rotation, _targetBaseRotation, rotationLerpSpeed * dt)
                : transform.rotation;
            transform.SetPositionAndRotation(pos, rot);
        }
    }

    /// <summary>
    /// Moves an immovable articulation root explicitly. This isolates the vehicle from
    /// impulses generated when a live TerrainCollider is rebuilt under its tracks.
    /// </summary>
    private void ApplyStabilizedBasePose(float dt)
    {
        Vector3 nextPosition = transform.position;
        Quaternion desiredRotation = GetStabilizedRotationTarget();
        Quaternion nextRotation = Quaternion.Slerp(
            transform.rotation,
            desiredRotation,
            1f - Mathf.Exp(-rotationLerpSpeed * dt));

        if (_positionTargetReady)
        {
            Vector3 desiredPosition = _targetBasePosition;
            if (snapToGround)
            {
                desiredPosition.y = SampleFootprintGroundY(
                    desiredPosition.x,
                    desiredPosition.z,
                    desiredRotation,
                    transform.position.y) + stabilizedGroundClearance;
            }

            if (_initialPositionTeleportPending)
            {
                nextPosition = desiredPosition;
                nextRotation = desiredRotation;
                _initialPositionTeleportPending = false;
            }
            else
            {
                float horizontalT = 1f - Mathf.Exp(-positionLerpSpeed * dt);
                float heightT = 1f - Mathf.Exp(-stabilizedHeightLerpSpeed * dt);
                nextPosition.x = Mathf.Lerp(transform.position.x, desiredPosition.x, horizontalT);
                nextPosition.z = Mathf.Lerp(transform.position.z, desiredPosition.z, horizontalT);
                nextPosition.y = Mathf.Lerp(transform.position.y, desiredPosition.y, heightT);
            }
        }

        _rootBody.TeleportRoot(nextPosition, nextRotation);
        _rootBody.velocity = Vector3.zero;
        _rootBody.angularVelocity = Vector3.zero;
    }

    private Quaternion GetStabilizedRotationTarget()
    {
        if (!keepStabilizedBaseUpright)
            return _rotationTargetReady ? _targetBaseRotation : transform.rotation;

        return _rotationTargetReady ? YawOnly(_targetBaseRotation) : _uprightBaseRotation;
    }

    private float SampleFootprintGroundY(
        float centerX,
        float centerZ,
        Quaternion baseRotation,
        float fallbackY)
    {
        float highest = SampleGroundYAtXZ(centerX, centerZ, fallbackY);
        float halfX = Mathf.Max(0f, stabilizedFootprintHalfExtents.x);
        float halfZ = Mathf.Max(0f, stabilizedFootprintHalfExtents.y);

        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iz = -1; iz <= 1; iz += 2)
            {
                Vector3 offset = baseRotation * new Vector3(ix * halfX, 0f, iz * halfZ);
                float y = SampleGroundYAtXZ(centerX + offset.x, centerZ + offset.z, fallbackY);
                if (y > highest) highest = y;
            }
        }

        return highest;
    }

    private static Quaternion YawOnly(Quaternion rotation)
    {
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        return forward.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up)
            : Quaternion.identity;
    }

    private void ApplyYawAngularVelocity()
    {
        Vector3 currentForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        Vector3 targetForward = Vector3.ProjectOnPlane(_targetBaseRotation * Vector3.forward, Vector3.up);
        if (currentForward.sqrMagnitude < 1e-6f || targetForward.sqrMagnitude < 1e-6f)
            return;

        float yawErrorDeg = Vector3.SignedAngle(currentForward, targetForward, Vector3.up);
        float yawVelocity = Mathf.Clamp(
            yawErrorDeg * Mathf.Deg2Rad * rotationLerpSpeed,
            -maxAngularVelocityRad,
            maxAngularVelocityRad);

        Vector3 angularVelocity = _rootBody.angularVelocity;
        angularVelocity.y = yawVelocity;
        _rootBody.angularVelocity = angularVelocity;
    }

    private void ApplyFullAngularVelocity()
    {
        Quaternion rotErr = _targetBaseRotation * Quaternion.Inverse(transform.rotation);
        rotErr.ToAngleAxis(out float angleDeg, out Vector3 axis);
        if (angleDeg > 180f) angleDeg -= 360f;
        if (axis.sqrMagnitude <= 0.001f)
        {
            _rootBody.angularVelocity = Vector3.zero;
            return;
        }

        Vector3 angularVelocity = axis.normalized
            * (angleDeg * Mathf.Deg2Rad * rotationLerpSpeed);
        float magnitude = angularVelocity.magnitude;
        if (magnitude > maxAngularVelocityRad)
            angularVelocity *= maxAngularVelocityRad / magnitude;
        _rootBody.angularVelocity = angularVelocity;
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

    private void UpdateJointAngleTargets()
    {
        float dt = Time.fixedDeltaTime;
        _commandedBoomAngle = Mathf.MoveTowardsAngle(
            _commandedBoomAngle,
            _targetBoomAngle,
            Mathf.Max(0f, boomSpeed) * dt);
        _commandedStickAngle = Mathf.MoveTowardsAngle(
            _commandedStickAngle,
            _targetStickAngle,
            Mathf.Max(0f, stickSpeed) * dt);
        _commandedBucketAngle = Mathf.MoveTowardsAngle(
            _commandedBucketAngle,
            _targetBucketAngle,
            Mathf.Max(0f, bucketSpeed) * dt);

        DriveToAngle(boom, _commandedBoomAngle);
        DriveToAngle(stick, _commandedStickAngle);
        DriveToAngle(bucket, _commandedBucketAngle);
    }

    private static float CurrentJointAngleDegrees(ArticulationBody body)
    {
        if (body == null || body.dofCount == 0)
            return 0f;

        // Imported revolute joints may report their zero pose as 2π instead of 0.
        // Normalize it so the first MQTT target does not command an unnecessary full turn.
        return Mathf.DeltaAngle(0f, body.jointPosition[0] * Mathf.Rad2Deg);
    }

    private string BuildUncontrolledJointDebug()
    {
        var result = new System.Text.StringBuilder();
        foreach (var body in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body == null
                || body.transform == transform
                || body == boom
                || body == stick
                || body == bucket)
                continue;

            if (result.Length > 0)
                result.Append(", ");

            result.Append(body.name)
                .Append(':')
                .Append(body.twistLock)
                .Append('/')
                .Append(FormatEuler(body.transform.localEulerAngles));
        }

        return result.ToString();
    }

    private static string FormatEuler(Vector3 euler)
    {
        return $"({euler.x:F1},{euler.y:F1},{euler.z:F1})";
    }

    private static float CalibrateJointAngle(JointAngleCalibration calibration, float sourceDegrees)
    {
        return calibration != null ? calibration.ToUnityDegrees(sourceDegrees) : sourceDegrees;
    }

    private static float ClampToJointLimits(ArticulationBody body, float angleDegrees)
    {
        if (body == null) return angleDegrees;

        var drive = body.xDrive;
        return drive.upperLimit > drive.lowerLimit
            ? Mathf.Clamp(angleDegrees, drive.lowerLimit, drive.upperLimit)
            : angleDegrees;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>以目标位置模式驱动关节到指定相对父关节角度（度）。</summary>
    private void DriveToAngle(ArticulationBody body, float targetAngleDegrees)
    {
        if (body == null) return;

        var drive = body.xDrive;
        drive.driveType = ArticulationDriveType.Target;
        drive.target = targetAngleDegrees;
        drive.targetVelocity = 0f;
        drive.stiffness = holdStiffness;
        drive.damping = holdDamping;
        drive.forceLimit = forceLimit;
        body.xDrive = drive;
    }

    /// <summary>以速度模式驱动关节；速度为 0 时锁定当前位置。</summary>
    void Drive(ArticulationBody body, float velocity)
    {
        if (body == null) return;

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
