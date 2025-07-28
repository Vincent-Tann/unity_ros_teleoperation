using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine.InputSystem;
using RosMessageTypes.Geometry;
using RosMessageTypes.Tf2;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using TimeMsg = RosMessageTypes.BuiltinInterfaces.TimeMsg;
using RosMessageTypes.Std;


public class HeadsetPublisherNewCopy : MonoBehaviour
{
    public string unityFrame = "vr_origin";
    public string headsetFrame = "headset";
    public string handFrameLeft = "hand_left";
    public string poseTopic = "/quest/pose";

    public InputActionReference headsetPose;
    public InputActionReference headsetRotation;
    public InputActionReference handPoseLeft;
    public InputActionReference handRotationLeft;
    public InputActionReference handPoseRight;
    public InputActionReference handRotationRight;

    private string handFrameRight = "hand_right";
    private ROSConnection ros;
    private TFMessageMsg tfMsg;
    private PoseStampedMsg headsetPoseMsg;
    private PoseStampedMsg leftHandMsg;
    private PoseStampedMsg rightHandMsg;

    private HeaderMsg headsetHeader;   // parent: vr_origin

    public string rosTimeTopic = "/ros_time_header";

    // ROS time offset: ros_time = unity_unix_seconds + rosOffset
    double rosOffset = 0.0;
    const double Alpha = 0.1;   // EMA smoothing

    private uint seq = 0;

    // static TimeMsg NowRosTime()
    // {
    //     var now = DateTime.UtcNow;
    //     long ticks = now.Ticks;                 // 1 tick = 100 ns
    //     uint sec  = (uint)new DateTimeOffset(now).ToUnixTimeSeconds();
    //     uint nsec = (uint)((ticks % TimeSpan.TicksPerSecond) * 100); // -> ns
    //     return new TimeMsg(sec, nsec);
    // }

    void Awake()
    {
        ros = ROSConnection.GetOrCreateInstance();

        ros.Subscribe<HeaderMsg>(rosTimeTopic, OnRosTimeHeader);

        handFrameRight = handFrameLeft.Replace("left", "right");

        ros.RegisterPublisher<PoseStampedMsg>(poseTopic + "/headset");
        ros.RegisterPublisher<TFMessageMsg>("/tf");

        headsetPoseMsg = new PoseStampedMsg();
        leftHandMsg    = new PoseStampedMsg();
        rightHandMsg   = new PoseStampedMsg();

        headsetHeader        = new HeaderMsg();
        headsetHeader.frame_id = unityFrame;

        tfMsg = new TFMessageMsg();
    }

    void OnRosTimeHeader(HeaderMsg msg)
    {
        // ROS 时间（秒）
        double rosNow = msg.stamp.sec + msg.stamp.nanosec * 1e-9;

        // Unity 当前 Unix 秒
        double unityNow = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        // 候选偏置（可能包含一次网络延迟误差，后续用 EMA 平滑）
        double candidate = rosNow - unityNow;

        // 限制异常跳变（可选）
        if (Math.Abs(candidate - rosOffset) > 0.5)   // 超过 0.5 s 认为是瞬时异常
            rosOffset = candidate;
        else
            rosOffset = (1 - Alpha) * rosOffset + Alpha * candidate;
    }

    static TimeMsg ToRosTime(double unixSeconds)
    {
        uint sec  = (uint)Math.Floor(unixSeconds);
        uint nsec = (uint)((unixSeconds - sec) * 1e9);
        return new TimeMsg(sec, nsec);
    }

    TimeMsg NowRosTime()
    {
        double unityNow = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        return ToRosTime(unityNow + rosOffset);
    }

    void Update()
    {
        /* Update header timestamp and sequence to avoid "TF_REPEATED_DATA" error */
        headsetHeader.stamp = NowRosTime();
        headsetHeader.seq   = seq++;

        /* Create TF array: 0=headset, 1=left hand, 2=right hand */
        tfMsg.transforms = new TransformStampedMsg[3];

        // headset ↔ vr_origin
        tfMsg.transforms[0] = new TransformStampedMsg();
        tfMsg.transforms[0].header         = headsetHeader;
        tfMsg.transforms[0].child_frame_id = headsetFrame;
        tfMsg.transforms[0].transform      = new TransformMsg();
        tfMsg.transforms[0].transform.translation = new Vector3Msg();
        tfMsg.transforms[0].transform.rotation = new QuaternionMsg();
        tfMsg.transforms[0].transform.rotation.w = 1;

        // left hand ↔ vr_origin
        tfMsg.transforms[1] = new TransformStampedMsg();
        tfMsg.transforms[1].header         = headsetHeader;
        tfMsg.transforms[1].child_frame_id = handFrameLeft;
        tfMsg.transforms[1].transform      = new TransformMsg();

        // right hand ↔ vr_origin
        tfMsg.transforms[2] = new TransformStampedMsg();
        tfMsg.transforms[2].header         = headsetHeader;
        tfMsg.transforms[2].child_frame_id = handFrameRight;
        tfMsg.transforms[2].transform      = new TransformMsg();

        /* Read device data */
        QuaternionMsg quaternion = headsetRotation.action.ReadValue<Quaternion>().To<FLU>();
        if (quaternion.From<FLU>().Equals(default)) quaternion.w = 1;

        tfMsg.transforms[0].transform.translation = headsetPose.action.ReadValue<Vector3>().To<FLU>();
        tfMsg.transforms[0].transform.rotation    = quaternion;

        tfMsg.transforms[1].transform.translation = handPoseLeft.action.ReadValue<Vector3>().To<FLU>();
        tfMsg.transforms[1].transform.rotation    = handRotationLeft.action.ReadValue<Quaternion>().To<FLU>();

        tfMsg.transforms[2].transform.translation = handPoseRight.action.ReadValue<Vector3>().To<FLU>();
        tfMsg.transforms[2].transform.rotation    = handRotationRight.action.ReadValue<Quaternion>().To<FLU>();

        /* Fallback to identity if tracker lost */
        for (int i = 0; i < tfMsg.transforms.Length; i++)
            if (tfMsg.transforms[i].transform.rotation.From<FLU>().Equals(default))
                tfMsg.transforms[i].transform.rotation.w = 1;
        
        headsetPoseMsg.header = headsetHeader;
        headsetPoseMsg.pose.orientation = quaternion;
        ros.Publish(poseTopic + "/headset", headsetPoseMsg);

        ros.Publish("/tf", tfMsg);
    }
}
