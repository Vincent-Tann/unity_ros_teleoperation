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
// ROS-TCP-Connector specific: TimeMsg is in BuiltinInterfaces namespace
// but behaves differently based on ROS1/ROS2 compilation flags
using RosMessageTypes.BuiltinInterfaces;

public class HeadsetPublisherNew : MonoBehaviour
{
    // --- Public Configuration ---
    [Header("TF and Frame Configuration")]
    public string unityFrame = "vr_origin";
    public string headsetFrame = "headset";
    public string handFrameLeft = "hand_left";

    [Header("Publishing Options")]
    // New boolean variable to control TF publishing.
    // If true, publishes both /tf and pose topics.
    // If false, publishes only pose topics.
    public bool send_tf = false;

    [Header("Input Action References")]
    public InputActionReference headsetPose;
    public InputActionReference headsetRotation;
    public InputActionReference handPoseLeft;
    public InputActionReference handRotationLeft;
    public InputActionReference handPoseRight;
    public InputActionReference handRotationRight;

    [Header("Time Synchronization")]
    public string rosTimeTopic = "/ros_time_header";

    // --- Private Variables ---
    private string handFrameRight;
    private ROSConnection ros;

    // ROS Message objects
    private TFMessageMsg tfMsg;
    private PoseStampedMsg headsetPoseMsg;
    private PoseStampedMsg leftHandMsg;
    private PoseStampedMsg rightHandMsg;

    // Time synchronization variables
    private double rosOffset = 0.0;
    private const double Alpha = 0.1; // EMA smoothing factor for time offset

    private uint seq = 0;

    void Awake()
    {
        ros = ROSConnection.GetOrCreateInstance();

        // Subscribe to a topic to get time from the ROS master for synchronization
        ros.Subscribe<HeaderMsg>(rosTimeTopic, OnRosTimeHeader);

        // Automatically determine the right hand frame name
        handFrameRight = handFrameLeft.Replace("left", "right");

        // --- Register all publishers ---
        // The /tf publisher will only be used if send_tf is true.
        ros.RegisterPublisher<TFMessageMsg>("/tf");
        // The pose topics will always be published. Note the updated topic names.
        ros.RegisterPublisher<PoseStampedMsg>("/Quest3/pose/headset");
        ros.RegisterPublisher<PoseStampedMsg>("/Quest3/pose/hand_left");
        ros.RegisterPublisher<PoseStampedMsg>("/Quest3/pose/hand_right");

        // --- Initialize message objects ---
        headsetPoseMsg = new PoseStampedMsg();
        leftHandMsg    = new PoseStampedMsg();
        rightHandMsg   = new PoseStampedMsg();
        tfMsg = new TFMessageMsg();
    }

    /// <summary>
    /// Callback for receiving time from ROS for synchronization.
    /// </summary>
    void OnRosTimeHeader(HeaderMsg msg)
    {
        // ROS-TCP-Connector: Uses 'nanosec' field for both ROS1 and ROS2 
        // (the package handles the conversion internally)
        // ROS time in seconds
        double rosNow = msg.stamp.sec + msg.stamp.nanosec * 1e-9;

        // Unity's current Unix time in seconds
        double unityNow = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        // Calculate a candidate for the time offset
        double candidate = rosNow - unityNow;

        // Use Exponential Moving Average (EMA) to smooth the offset and reduce jitter
        // from network latency spikes.
        if (Math.Abs(candidate - rosOffset) > 0.5 && rosOffset != 0.0) // Reset if jump is too large
            rosOffset = candidate;
        else
            rosOffset = (1 - Alpha) * rosOffset + Alpha * candidate;
    }

    /// <summary>
    /// Converts a double representing Unix seconds to a ROS TimeMsg.
    /// ROS-TCP-Connector handles ROS1/ROS2 differences internally.
    /// </summary>
    static TimeMsg ToRosTime(double unixSeconds)
    {
        uint sec  = (uint)Math.Floor(unixSeconds);
        uint nanosec = (uint)((unixSeconds - sec) * 1e9);
        // ROS-TCP-Connector: TimeMsg constructor takes (sec, nanosec) for both ROS1/ROS2
        return new TimeMsg(sec, nanosec);
    }

    /// <summary>
    /// Gets the estimated current ROS time by adding the calculated offset to Unity's time.
    /// </summary>
    TimeMsg GetSynchronizedRosTime()
    {
        double unityNow = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        return ToRosTime(unityNow + rosOffset);
    }

    void Update()
    {
        // --- 1. Get current synchronized time and sequence number ---
        // Create a single header for this update frame to ensure all messages are consistent.
        var currentHeader = new HeaderMsg(seq++, GetSynchronizedRosTime(), unityFrame);

        // --- 2. Read all device data from Input Actions ---
        // Convert from Unity's coordinate system to ROS's (FLU)
        var headsetPos = headsetPose.action.ReadValue<Vector3>().To<FLU>();
        var headsetRot = headsetRotation.action.ReadValue<Quaternion>().To<FLU>();

        var leftHandPos = handPoseLeft.action.ReadValue<Vector3>().To<FLU>();
        var leftHandRot = handRotationLeft.action.ReadValue<Quaternion>().To<FLU>();

        var rightHandPos = handPoseRight.action.ReadValue<Vector3>().To<FLU>();
        var rightHandRot = handRotationRight.action.ReadValue<Quaternion>().To<FLU>();

        // Fallback to identity quaternion if tracking is lost to avoid invalid data
        if (headsetRot.Equals(default)) headsetRot.w = 1;
        if (leftHandRot.Equals(default)) leftHandRot.w = 1;
        if (rightHandRot.Equals(default)) rightHandRot.w = 1;

        // --- 3. Always populate and publish PoseStamped messages ---
        // Headset
        headsetPoseMsg.header = currentHeader;
        headsetPoseMsg.pose.position = headsetPos;
        headsetPoseMsg.pose.orientation = headsetRot;
        ros.Publish("/Quest3/pose/headset", headsetPoseMsg);

        // Left Hand
        leftHandMsg.header = currentHeader;
        leftHandMsg.pose.position = leftHandPos;
        leftHandMsg.pose.orientation = leftHandRot;
        ros.Publish("/Quest3/pose/hand_left", leftHandMsg);

        // Right Hand
        rightHandMsg.header = currentHeader;
        rightHandMsg.pose.position = rightHandPos;
        rightHandMsg.pose.orientation = rightHandRot;
        ros.Publish("/Quest3/pose/hand_right", rightHandMsg);

        // --- 4. Conditionally populate and publish the /tf message ---
        if (send_tf)
        {
            // Create a new transform array for the TF message
            tfMsg.transforms = new TransformStampedMsg[3];

            // Headset Transform
            tfMsg.transforms[0] = new TransformStampedMsg(currentHeader, headsetFrame, new TransformMsg(headsetPos, headsetRot));

            // Left Hand Transform
            tfMsg.transforms[1] = new TransformStampedMsg(currentHeader, handFrameLeft, new TransformMsg(leftHandPos, leftHandRot));

            // Right Hand Transform
            tfMsg.transforms[2] = new TransformStampedMsg(currentHeader, handFrameRight, new TransformMsg(rightHandPos, rightHandRot));

            // Publish the single TF message containing all three transforms
            ros.Publish("/tf", tfMsg);
        }
    }
}