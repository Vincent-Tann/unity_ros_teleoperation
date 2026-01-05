/*
 * LeftHandControllerControl.cs
 * ------------------------------------------------------------
 * Provides controller-based control for a teleoperation system
 * using LEFT controller button inputs as an alternative to hand gestures.
 *
 * ▸ 🎯  Trigger press                     →  Toggle Pause/Resume
 * ▸ 🅰️  A button press                   →  Trigger calibration  
 * ▸ 🅱️  B button press                   →  Trigger go home
 *
 * This script mirrors the functionality of LeftHandGestureControl
 * but uses controller inputs instead of hand tracking gestures.
 * Both scripts can coexist and publish to the same ROS topics.
 *
 * A TextMeshProUGUI field gives real-time status feedback in VR:
 *   Running  /  Paused  /  Calibration Triggered  /  Go Home Triggered
 * ------------------------------------------------------------
 */

using UnityEngine;
using UnityEngine.XR;
using TMPro;
// Add ROS imports for Unity-Robotics-Hub
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using System.Collections.Generic;

public class LeftHandControllerControl : MonoBehaviour
{
    // ─────────────────────── Inspector ───────────────────────
    [Header("UI")]
    [Tooltip("Text element shown inside VR for status feedback")]
    public TextMeshProUGUI statusText;

    [Header("Button Mappings")]
    [Tooltip("Trigger press toggles pause/resume")]
    public bool enableTriggerPauseToggle = true;
    
    [Tooltip("A button triggers calibration")]
    public bool enableACalibration = true;
    
    [Tooltip("B button triggers go home")]
    public bool enableBGoHome = true;

    [Header("Timing (seconds)")]
    [Tooltip("Ignore further button presses for this long after a press")]
    public float buttonCooldown = 0.3f;

    [Tooltip("Cooldown time after calibration to prevent frequent triggers")]
    public float calibrationCooldown = 3f;

    [Tooltip("How long to show 'Calibration Triggered' message")]
    public float calibrationConfirmDuration = 1f;

    [Tooltip("Cooldown time after go home to prevent frequent triggers")]
    public float goHomeCooldown = 3f;

    [Tooltip("How long to show 'Go Home Triggered' message")]
    public float goHomeConfirmDuration = 1f;

    // ─────────────────────── Private state ───────────────────────
    private InputDevice leftController;
    private bool isControllerValid = false;
    
    // Button state tracking
    private bool lastTriggerState = false;
    private bool lastAButtonState = false;
    private bool lastBButtonState = false;
    
    // Timing controls
    private float lastButtonPressTime = 0f;
    private float calibrationCooldownUntil = 0f;
    private float calibrationTriggeredUntil = 0f;
    private float goHomeCooldownUntil = 0f;
    private float goHomeTriggeredUntil = 0f;
    
    // State
    private bool isPaused = true;  // Start in paused state to match gesture control
    
    // ROS publishers (same topics as gesture control)
    private ROSConnection ros;
    private string pauseTopicName = "/Quest3/isPaused"; // Bool
    private string calibrationTopicName = "/Quest3/requireCalibration"; // Empty
    private string goHomeTopicName = "/Quest3/requireGoHome"; // Empty
    private bool lastPublishedPauseState = true;

    // Log throttling
    private float lastLogTime = -999f;
    private const float LOG_COOLDOWN = 2.0f;

    // ─────────────────────── Unity lifecycle ───────────────────────
    void Start()
    {
        InitializeController();
        InitializeROS();
        SetStatusText("<color=white>[Controller] Stopped</color>");
    }

    void Update()
    {
        // Check controller validity and reconnect if needed
        if (!isControllerValid || !leftController.isValid)
        {
            InitializeController();
        }

        if (!isControllerValid) return;

        // Process button inputs
        ProcessButtonInputs();
        
        // Update status display
        UpdateStatusDisplay();
    }

    void OnEnable()
    {
        // Listen for device connect/disconnect events
        InputDevices.deviceConnected += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
    }

    void OnDisable()
    {
        // Cleanup event listeners
        InputDevices.deviceConnected -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
    }

    // ─────────────────────── Controller Management ───────────────────────
    void InitializeController()
    {
        var leftHandDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftHandDevices);
        
        if (leftHandDevices.Count > 0)
        {
            leftController = leftHandDevices[0];
            isControllerValid = leftController.isValid;
            
            if (isControllerValid && Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
            {
                Debug.Log($"[LeftHandControllerControl] Left controller found: {leftController.name}");
                lastLogTime = Time.unscaledTime;
            }
        }
        else
        {
            isControllerValid = false;
            if (Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
            {
                Debug.Log("[LeftHandControllerControl] Left controller not found (may be in hand tracking mode)");
                lastLogTime = Time.unscaledTime;
            }
        }
    }

    private void OnDeviceConnected(InputDevice device)
    {
        if (Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
        {
            Debug.Log($"[LeftHandControllerControl] Device connected: {device.name} - Reinitializing");
            lastLogTime = Time.unscaledTime;
        }
        InitializeController();
    }

    private void OnDeviceDisconnected(InputDevice device)
    {
        if (Time.unscaledTime - lastLogTime > LOG_COOLDOWN)
        {
            Debug.Log($"[LeftHandControllerControl] Device disconnected: {device.name} - Reinitializing");
            lastLogTime = Time.unscaledTime;
        }
        InitializeController();
    }

    // ─────────────────────── ROS Initialization ───────────────────────
    void InitializeROS()
    {
        // Get ROS connection instance
        ros = ROSConnection.GetOrCreateInstance();
        
        // Register publishers for the same topics as gesture control
        ros.RegisterPublisher<BoolMsg>(pauseTopicName);
        ros.RegisterPublisher<EmptyMsg>(calibrationTopicName);
        ros.RegisterPublisher<EmptyMsg>(goHomeTopicName);
        
        // Publish initial state
        PublishPauseState();
        
        Debug.Log($"[LeftHandControllerControl] ROS publishers initialized for topics: {pauseTopicName}, {calibrationTopicName}, {goHomeTopicName}");
    }

    // ─────────────────────── Button Input Processing ───────────────────────
    void ProcessButtonInputs()
    {
        // Skip if we're in a button cooldown period
        if (Time.time - lastButtonPressTime < buttonCooldown) return;

        // Read current button states
        bool currentTriggerState = false;
        bool currentAButtonState = false;
        bool currentBButtonState = false;

        leftController.TryGetFeatureValue(CommonUsages.triggerButton, out currentTriggerState);
        leftController.TryGetFeatureValue(CommonUsages.primaryButton, out currentAButtonState);   // A button
        leftController.TryGetFeatureValue(CommonUsages.secondaryButton, out currentBButtonState); // B button

        // Detect button press events (transition from false to true)
        bool triggerPressed = currentTriggerState && !lastTriggerState;
        bool aButtonPressed = currentAButtonState && !lastAButtonState;
        bool bButtonPressed = currentBButtonState && !lastBButtonState;

        // Process trigger for pause toggle
        if (triggerPressed && enableTriggerPauseToggle)
        {
            TogglePauseState();
            lastButtonPressTime = Time.time;
        }

        // Process A button for calibration
        if (aButtonPressed && enableACalibration && Time.time >= calibrationCooldownUntil)
        {
            TriggerCalibration();
            lastButtonPressTime = Time.time;
        }

        // Process B button for go home
        if (bButtonPressed && enableBGoHome && Time.time >= goHomeCooldownUntil)
        {
            TriggerGoHome();
            lastButtonPressTime = Time.time;
        }

        // Update last button states
        lastTriggerState = currentTriggerState;
        lastAButtonState = currentAButtonState;
        lastBButtonState = currentBButtonState;
    }

    // ─────────────────────── State Control ───────────────────────
    void TogglePauseState()
    {
        isPaused = !isPaused;

        if (isPaused)
        {
            Debug.Log("<color=white>[Controller] Paused</color>");
        }
        else
        {
            Debug.Log("<color=green>[Controller] Resumed</color>");
        }
        
        // Publish pause state change
        PublishPauseState();
    }

    void TriggerCalibration()
    {
        calibrationTriggeredUntil = Time.time + calibrationConfirmDuration;
        calibrationCooldownUntil = Time.time + calibrationCooldown;

        Debug.Log("<color=#00FFFF>[Controller] Calibration Triggered</color>");

        // Publish calibration trigger via ROS
        if (ros != null)
        {
            ros.Publish(calibrationTopicName, new EmptyMsg());
            Debug.Log($"[LeftHandControllerControl] Published calibration trigger to {calibrationTopicName}");
        }
    }

    void TriggerGoHome()
    {
        goHomeTriggeredUntil = Time.time + goHomeConfirmDuration;
        goHomeCooldownUntil = Time.time + goHomeCooldown;

        Debug.Log("<color=orange>[Controller] Go Home Triggered</color>");

        // Publish go home trigger via ROS
        if (ros != null)
        {
            ros.Publish(goHomeTopicName, new EmptyMsg());
            Debug.Log($"[LeftHandControllerControl] Published go home trigger to {goHomeTopicName}");
        }
    }

    // ─────────────────────── ROS Publishing ───────────────────────
    void PublishPauseState()
    {
        // Only publish if state has changed to avoid spamming
        if (ros != null && lastPublishedPauseState != isPaused)
        {
            BoolMsg pauseMsg = new BoolMsg
            {
                data = isPaused
            };
            
            ros.Publish(pauseTopicName, pauseMsg);
            lastPublishedPauseState = isPaused;
            
            Debug.Log($"[LeftHandControllerControl] Published pause state: {isPaused} to {pauseTopicName}");
        }
    }

    // ─────────────────────── Status Display ───────────────────────
    void UpdateStatusDisplay()
    {
        if (!isControllerValid)
        {
            SetStatusText("<color=gray>[Controller] Not Connected</color>");
            return;
        }

        // Show status with priority: Go Home Triggered > Calibration Triggered > Go Home Cooldown > Calibration Cooldown > Normal
        if (Time.time < goHomeTriggeredUntil)
        {
            SetStatusText("<color=orange>[Controller] Go Home Triggered</color>");
        }
        else if (Time.time < calibrationTriggeredUntil)
        {
            SetStatusText("<color=#00FFFF>[Controller] Calibration Triggered</color>");
        }
        else if (Time.time < goHomeCooldownUntil)
        {
            float remainingCooldown = goHomeCooldownUntil - Time.time;
            SetStatusText($"<color=orange>[Controller] Go Home Cooldown ({remainingCooldown:F1}s)</color>");
        }
        else if (Time.time < calibrationCooldownUntil)
        {
            float remainingCooldown = calibrationCooldownUntil - Time.time;
            SetStatusText($"<color=#00FFFF>[Controller] Calibration Cooldown ({remainingCooldown:F1}s)</color>");
        }
        else
        {
            string textColor = isPaused ? "white" : "green";
            string status = isPaused ? "Paused" : "Running";
            SetStatusText($"<color={textColor}>[Controller] {status}</color>");
        }
    }

    // ─────────────────────── UI helper ───────────────────────
    void SetStatusText(string txt)
    {
        if (statusText != null)
            statusText.text = txt;
    }
} 