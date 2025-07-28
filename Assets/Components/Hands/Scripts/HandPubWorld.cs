using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.Geometry;
using RosMessageTypes.Sensor;
using RosMessageTypes.RetargetingRos; // Kept for message definition compatibility
using TMPro;
using UnityEngine.InputSystem;

public class HandPubWorld : MonoBehaviour
{
    // Maps openxr joint indices to mano joint indices
    private static Dictionary<int, XRHandJointID> jointMap = new Dictionary<int, XRHandJointID> {
        {0, XRHandJointID.Wrist},
        {1, XRHandJointID.ThumbMetacarpal},
        {2, XRHandJointID.ThumbProximal},
        {3, XRHandJointID.ThumbDistal},
        {4, XRHandJointID.ThumbTip},
        {5, XRHandJointID.IndexProximal},
        {6, XRHandJointID.IndexIntermediate},
        {7, XRHandJointID.IndexDistal},
        {8, XRHandJointID.IndexTip},
        {9, XRHandJointID.MiddleProximal},
        {10, XRHandJointID.MiddleIntermediate},
        {11, XRHandJointID.MiddleDistal},
        {12, XRHandJointID.MiddleTip},
        {13, XRHandJointID.RingProximal},
        {14, XRHandJointID.RingIntermediate},
        {15, XRHandJointID.RingDistal},
        {16, XRHandJointID.RingTip},
        {17, XRHandJointID.LittleProximal},
        {18, XRHandJointID.LittleIntermediate},
        {19, XRHandJointID.LittleDistal},
        {20, XRHandJointID.LittleTip}
    };

    // Input action for simple gesture publishing (optional)
    public InputActionReference activeController;
    
    // UI element for displaying status information
    public TextMeshProUGUI infoText;

    ROSConnection ros;
    XRHandSubsystem m_handSubsystem;

    // Flag to filter for high-confidence tracking data only
    private bool _highConfidence = false;

    // ROS topic names
    private const string _baseTopic = "/Quest3";
    private const string _landmarksTopic = _baseTopic + "/hand_pose";
    private const string _pointCloudTopic = _baseTopic + "/hand_points";
    private const string _gestureTopic = _baseTopic + "/hand_gesture";

    // Frame ID for the ROS message headers
    public string worldFrame = "world"; // Changed to "world" or "unity_world" to reflect the coordinate system


    void Start()
    {
        // Find the running XRHandSubsystem
        var _handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(_handSubsystems);
        Debug.Log("Found " + _handSubsystems.Count + " hand subsystems");
        foreach (var hand in _handSubsystems)
        {
            if(hand.running)
            {
                m_handSubsystem = hand;
                break;
            }
        }

        if(m_handSubsystem == null)
        {
            Debug.LogError("No running hand subsystem found");
        } 
        else 
        {
            Debug.Log("Found running hand subsystem");
            // Subscribe to the hand update event
            m_handSubsystem.updatedHands += OnHandUpdate;

            // Optional: check if the joints in this layout contain the joints we need
            var joints = m_handSubsystem.jointsInLayout;
            for (int i=0; i<jointMap.Count; i++)
            {
                // This check might need adjustment depending on the full layout size
                if (jointMap.ContainsKey(i) && !joints[i])
                {
                    Debug.LogWarning("Joint " + jointMap[i] + " (index " + i + ") not found in joint layout.");
                }
            }
        }

        // Initialize ROS connection and register publishers
        ros = ROSConnection.GetOrCreateInstance();
        // ros.RegisterPublisher<ManoLandmarksMsg>(_landmarksTopic);
        ros.RegisterPublisher<PointCloudMsg>(_pointCloudTopic);
        ros.RegisterPublisher<HandGestureMsg>(_gestureTopic);
    }

    void Update()
    {
        // Publish a simple gesture if the activeController action is performed
        if (activeController != null && activeController.action.ReadValue<float>() > 0.5f)
        {
            PubActiveController();
        } 
        else 
        {
            if(infoText != null)
                infoText.color = Color.red;
        }
    }

    // Publishes a "Closed_Fist" gesture. Can be triggered by controller input.
    public void PubActiveController()
    {
        HandGestureMsg msg = new HandGestureMsg();
        msg.name = "Closed_Fist";
        ros.Publish(_gestureTopic, msg);

        if(infoText != null)
            infoText.color = Color.green;
    }
    
    // Toggles whether to only use high-confidence tracking updates
    public void ToggleConfidence()
    {
        _highConfidence = !_highConfidence;
        infoText?.SetText(_highConfidence ? "High Confidence" : "Low Confidence");
    }

    // Callback executed every time the hand subsystem updates
    void OnHandUpdate(XRHandSubsystem subsystem, 
        XRHandSubsystem.UpdateSuccessFlags updateSuccessFlags,
        XRHandSubsystem.UpdateType updateType)
    {
        // If high confidence is required, ignore updates that are not fully successful
        if(_highConfidence && updateSuccessFlags != XRHandSubsystem.UpdateSuccessFlags.All) return;
        
        // Use dynamic updates which occur every frame, bypassing less frequent render updates
        if(updateType != XRHandSubsystem.UpdateType.Dynamic) return;
        
        // Prepare ROS messages
        // ManoLandmarksMsg msg = new ManoLandmarksMsg();
        PointCloudMsg pointCloudMsg = new PointCloudMsg();
        HeaderMsg header = new HeaderMsg();
        header.frame_id = worldFrame;
        // msg.header = header;
        pointCloudMsg.header = header;
        
        ChannelFloat32Msg[] channels = new ChannelFloat32Msg[1];
        channels[0] = new ChannelFloat32Msg();
        channels[0].name = "intensity";
        channels[0].values = new float[jointMap.Count];
        Point32Msg[] points = new Point32Msg[jointMap.Count];

        // Process the right hand if it is tracked
        XRHand hand = subsystem.rightHand;
        if(hand.isTracked)
        {
            int trackedJointCount = 0;
            foreach (int i in jointMap.Keys)
            {
                var jointID = jointMap[i];
                var joint = hand.GetJoint(jointID);

                // Try to get the pose (position and rotation) of the joint
                if(joint.TryGetPose(out Pose pose))
                {
                    if(points[i] == null)
                        points[i] = new Point32Msg();

                    // The pose is already in world coordinates, no PoseManager needed.
                    // Directly use pose.position.
                    
                    // Swizzle coordinates for ROS standard (Y-up to Z-up)
                    points[i].x = pose.position.x;
                    points[i].y = pose.position.z;
                    points[i].z = pose.position.y;
                    
                    channels[0].values[i] = 1; // Mark this point as valid
                    trackedJointCount++;
                } 
                else 
                {
                    Debug.LogWarning("Failed to get pose for joint " + jointID);
                    channels[0].values[i] = 0; // Mark this point as invalid
                }
            }

            // Publish messages if joints were tracked
            if (trackedJointCount > 0)
            {
                pointCloudMsg.points = points;
                pointCloudMsg.channels = channels;
                // msg.landmarks = CastPoints(points);
                // ros.Publish(_landmarksTopic, msg);
                ros.Publish(_pointCloudTopic, pointCloudMsg);
            }
        } 
    }

    // Utility function to cast Point32Msg array to PointMsg array for the ManoLandmarksMsg
    public static PointMsg[] CastPoints(Point32Msg[] points)
    {
        PointMsg[] castedPoints = new PointMsg[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            castedPoints[i] = new PointMsg();
            if(points[i] == null) continue;
            castedPoints[i].x = points[i].x;
            castedPoints[i].y = points[i].y;
            castedPoints[i].z = points[i].z;
        }
        return castedPoints;
    }

}