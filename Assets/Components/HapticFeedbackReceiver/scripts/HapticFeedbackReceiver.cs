// HapticFeedbackReceiver.cs
using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using System.Collections.Generic;

public class HapticFeedbackReceiver : MonoBehaviour
{
    [Header("ROS Topics")]
    [SerializeField]
    private string leftHandHapticTopic = "/Quest3/haptics/left";
    [SerializeField]
    private string rightHandHapticTopic = "/Quest3/haptics/right";

    // XR Input Devices
    private InputDevice leftController;
    private InputDevice rightController;

    // Log throttling
    private float lastLogTime = -999f;
    private const float LOG_COOLDOWN = 2.0f;  // 2秒日志冷却

    // Controller state tracking to prevent duplicate logs
    private bool lastLeftControllerState = false;
    private bool lastRightControllerState = false;

    void Start()
    {
        // Get the ROS connection instance
        ROSConnection.GetOrCreateInstance();
        
        // Subscribe to the haptic command topics for both controllers
        ROSConnection.instance.Subscribe<Float32MultiArrayMsg>(leftHandHapticTopic, (msg) => OnHapticCommand(msg, XRNode.LeftHand));
        ROSConnection.instance.Subscribe<Float32MultiArrayMsg>(rightHandHapticTopic, (msg) => OnHapticCommand(msg, XRNode.RightHand));

        // Initialize and get the XR controller devices
        InitializeControllers();
    }

    void InitializeControllers()
    {
        bool shouldLog = Time.unscaledTime - lastLogTime > LOG_COOLDOWN;

        // Attempt to get the left controller
        var leftHandDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);
        bool leftFound = leftHandDevices.Count > 0;
        
        if (leftFound)
        {
            leftController = leftHandDevices[0];
            if (shouldLog && (!lastLeftControllerState || !leftController.isValid))
            {
                Debug.Log("Left hand controller found: " + leftController.name);
                lastLogTime = Time.unscaledTime;
            }
        }
        else
        {
            if (shouldLog && lastLeftControllerState)
            {
                Debug.Log("Left hand controller not found (may be in hand tracking mode)");
                lastLogTime = Time.unscaledTime;
            }
        }
        lastLeftControllerState = leftFound;

        // Attempt to get the right controller
        var rightHandDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightHandDevices);
        bool rightFound = rightHandDevices.Count > 0;
        
        if (rightFound)
        {
            rightController = rightHandDevices[0];
            if (shouldLog && (!lastRightControllerState || !rightController.isValid))
            {
                Debug.Log("Right hand controller found: " + rightController.name);
                lastLogTime = Time.unscaledTime;
            }
        }
        else
        {
            if (shouldLog && lastRightControllerState)
            {
                Debug.Log("Right hand controller not found (may be in hand tracking mode)");
                lastLogTime = Time.unscaledTime;
            }
        }
        lastRightControllerState = rightFound;
    }

    void OnEnable()
    {
        // Listen for controller connect/disconnect events
        InputDevices.deviceConnected += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
    }

    void OnDisable()
    {
        // Cleanup event listeners
        InputDevices.deviceConnected -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
    }

    private void OnDeviceConnected(InputDevice device)
    {
        if (Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
        {
            Debug.Log($"Device connected: {device.name} - Reinitializing controllers");
            lastLogTime = Time.unscaledTime;
        }
        InitializeControllers();
    }

    private void OnDeviceDisconnected(InputDevice device)
    {
        if (Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
        {
            Debug.Log($"Device disconnected: {device.name} - Reinitializing controllers");
            lastLogTime = Time.unscaledTime;
        }
        InitializeControllers();
    }

    /// <summary>
    /// Callback function executed when a ROS message is received on a subscribed topic.
    /// </summary>
    /// <param name="msg">The received ROS message.</param>
    /// <param name="handNode">The target controller (LeftHand or RightHand).</param>
    private void OnHapticCommand(Float32MultiArrayMsg msg, XRNode handNode)
    {
        // --- Data Validation ---
        // The message data must contain at least two values: amplitude and duration.
        if (msg.data == null || msg.data.Length < 2)
        {
            Debug.LogWarning($"Received invalid haptic command on topic for {handNode}. Data array is null or too short.");
            return;
        }

        // Parse amplitude and duration from the message
        float amplitude = msg.data[0];
        float duration = msg.data[1];

        // Clamp the amplitude to the valid [0, 1] range.
        amplitude = Mathf.Clamp01(amplitude);

        // --- Select target device and send haptic command ---
        InputDevice targetDevice = (handNode == XRNode.LeftHand) ? leftController : rightController;

        // Ensure the device is valid and connected.
        if (targetDevice.isValid)
        {
            // Get the device's haptic capabilities.
            HapticCapabilities capabilities;
            if (targetDevice.TryGetHapticCapabilities(out capabilities))
            {
                // Check if it supports impulse-based haptics.
                if (capabilities.supportsImpulse)
                {
                    // Send the haptic impulse command.
                    // Channel 0 is typically the main haptic motor.
                    targetDevice.SendHapticImpulse(0, amplitude, duration);
                    Debug.Log($"Sent haptic impulse to {handNode}: Amplitude={amplitude}, Duration={duration}s");
                }
            }
        }
        else
        {
            // If the device is not valid (e.g., controller is off or temporarily disconnected), try to re-initialize.
            Debug.LogWarning($"{handNode} controller is not valid. Re-initializing...");
            InitializeControllers();
        }
    }

    void Update()
    {
        // Periodically check in Update in case controllers are connected after the game starts.
        if (!leftController.isValid || !rightController.isValid)
        {
            InitializeControllers();
        }
    }
}