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

    private float _targetCabin;
    private float _targetBoom;
    private float _targetStick;
    private float _targetBucket;

    /// <summary>
    /// 由 MqttManager 调用，传入各关节相对前一关节的目标角度（度）。
    /// 调用后自动切换为 MQTT 控制模式。
    /// </summary>
    public void ApplyJointControl(float cabinAngle, float boomAngle, float stickAngle, float bucketAngle)
    {
        _targetCabin  = cabinAngle;
        _targetBoom   = boomAngle;
        _targetStick  = stickAngle;
        _targetBucket = bucketAngle;
        mqttControlEnabled = true;
    }

    void FixedUpdate()
    {
        if (mqttControlEnabled)
        {
            DriveToAngle(cabin,  _targetCabin);
            DriveToAngle(boom,   _targetBoom);
            DriveToAngle(stick,  _targetStick);
            DriveToAngle(bucket, _targetBucket);
        }
        else
        {
            float cabinInput = 0f;
            if (Input.GetKey(KeyCode.A)) cabinInput =  cabinSpeed;
            if (Input.GetKey(KeyCode.D)) cabinInput = -cabinSpeed;
            Drive(cabin, cabinInput);

            float boomInput = 0f;
            if (Input.GetKey(KeyCode.W)) boomInput =  boomSpeed;
            if (Input.GetKey(KeyCode.S)) boomInput = -boomSpeed;
            Drive(boom, boomInput);

            float stickInput = 0f;
            if (Input.GetKey(KeyCode.UpArrow))   stickInput = -stickSpeed;
            if (Input.GetKey(KeyCode.DownArrow))  stickInput =  stickSpeed;
            Drive(stick, stickInput);

            float bucketInput = 0f;
            if (Input.GetKey(KeyCode.LeftArrow))  bucketInput = -bucketSpeed;
            if (Input.GetKey(KeyCode.RightArrow)) bucketInput =  bucketSpeed;
            Drive(bucket, bucketInput);
        }
    }

    /// <summary>以目标位置模式驱动关节到指定角度（度）。</summary>
    void DriveToAngle(ArticulationBody body, float targetAngleDeg)
    {
        var drive = body.xDrive;
        drive.driveType  = ArticulationDriveType.Target;
        drive.target     = targetAngleDeg;
        drive.stiffness  = holdStiffness;
        drive.damping    = holdDamping;
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
            drive.target    = body.jointPosition[0] * Mathf.Rad2Deg;
            drive.stiffness = holdStiffness;
            drive.damping   = holdDamping;
        }
        else
        {
            drive.driveType      = ArticulationDriveType.Velocity;
            drive.targetVelocity = velocity;
            drive.stiffness      = 0f;
            drive.damping        = moveDamping;
        }

        body.xDrive = drive;
    }
}