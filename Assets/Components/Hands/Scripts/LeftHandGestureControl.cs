/*
 * LeftHandGestureControl.cs
 * ------------------------------------------------------------
 * Adds gesture-based control for a teleoperation system
 * using LEFT-hand gestures tracked by the XR Hands API.
 *
 * ▸ ✊  Hold fist ≥ holdDuration  →  Pause
 * ▸ ✋  Hold open hand ≥ holdDuration  →  Resume
 * ▸ ✌️  Hold sword finger ≥ calibrationHoldDuration  →  Trigger calibration
 * ▸ 🤟  Hold three fingers (index, middle, ring) ≥ goHomeHoldDuration  →  Trigger go home
 *
 * Four distance thresholds are exposed in the Inspector so
 * designers can tune them for different hand sizes or lighting.
 *
 * A TextMeshProUGUI field gives real-time status feedback in VR:
 *   Running  /  Paused  /  Confirming Pause…  /  Confirming Resume…  /  Confirming Calibration…  /  Confirming Go Home…
 *
 * Replace the TODO calls inside SetPaused() with your own
 * teleoperation-pause / resume hooks or ROS publishers.
 * ------------------------------------------------------------
 */

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;
// Add ROS imports for Unity-Robotics-Hub
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

public class LeftHandGestureControl : MonoBehaviour
{
    // ─────────────────────── Inspector ───────────────────────
    [Header("UI")]
    [Tooltip("Text element shown inside VR for status feedback")]
    public TextMeshProUGUI statusText;

    [Header("Finger-Wrist distance thresholds (metres)")]
    [Tooltip("Index/Middle/Ring/Little ≤ value ⇒ considered 'curled'")]
    public float fingerCurlThreshold   = 0.1f;

    [Tooltip("Index/Middle/Ring/Little ≥ value ⇒ considered 'extended'")]
    public float fingerExtendThreshold = 0.15f;

    [Tooltip("Thumb ≤ value ⇒ considered 'curled'")]
    public float thumbCurlThreshold    = 0.13f;

    [Tooltip("Thumb ≥ value ⇒ considered 'extended'")]
    public float thumbExtendThreshold  = 0.13f;

    [Header("Hand-Headset distance control")]
    [Tooltip("Hand must be within this distance from headset for open-hand detection")]
    public float maxHandHeadsetDistance = 0.4f; // 40cm

    [Header("Timing (seconds)")]
    [Tooltip("How long the gesture must be held to trigger")]
    public float holdDuration   = 1f;

    [Tooltip("Ignore further switches for this long after a change")]
    public float postSwitchLock = 2f;

    [Header("Calibration timing (seconds)")]
    [Tooltip("How long sword finger must be held to trigger calibration")]
    public float calibrationHoldDuration = 0.5f;

    [Tooltip("Cooldown time after calibration to prevent frequent triggers")]
    public float calibrationCooldown = 3f;

    [Tooltip("How long to show 'Calibration Triggered' message before showing cooldown")]
    public float calibrationConfirmDuration = 1f;

    [Header("Go Home timing (seconds)")]
    [Tooltip("How long three fingers must be held to trigger go home")]
    public float goHomeHoldDuration = 0.8f;

    [Tooltip("Cooldown time after go home to prevent frequent triggers")]
    public float goHomeCooldown = 3f;

    [Tooltip("How long to show 'Go Home Triggered' message before showing cooldown")]
    public float goHomeConfirmDuration = 1f;

    // ─────────────────────── Private state ───────────────────────
    XRHandSubsystem subsystem;

    float fistTimer = 0f;      // Time the fist has been continuously detected
    float openTimer = 0f;      // Time the open hand has been continuously detected
    float swordFingerTimer = 0f; // Time the sword finger has been continuously detected
    float threeFingerTimer = 0f; // Time the three fingers have been continuously detected
    float lockUntil = 0f;      // Temporal lock to suppress rapid toggles
    float calibrationCooldownUntil = 0f; // Calibration cooldown end time
    float calibrationTriggeredUntil = 0f; // Show "Calibration Triggered" until this time
    float goHomeCooldownUntil = 0f; // Go home cooldown end time
    float goHomeTriggeredUntil = 0f; // Show "Go Home Triggered" until this time

    bool isPaused = true;     // Current teleop state
    
    // ROS publishers
    ROSConnection ros;
    string pauseTopicName = "/Quest3/isPaused";
    string calibrationTopicName = "/Quest3/requireCalibration";
    string goHomeTopicName = "/Quest3/requireGoHome";
    bool lastPublishedPauseState = true; // Track last published state to avoid redundant messages

    // Only four fingertips for quick loops
    readonly XRHandJointID[] fingerTips = {
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingTip,
        XRHandJointID.LittleTip
    };

    // Sword finger specific joints (index and middle only)
    readonly XRHandJointID[] swordFingerTips = {
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip
    };

    // Non-sword finger joints (ring, little, thumb)
    readonly XRHandJointID[] nonSwordFingerTips = {
        XRHandJointID.RingTip,
        XRHandJointID.LittleTip
    };

    // Three finger specific joints (index, middle, ring)
    readonly XRHandJointID[] threeFingerTips = {
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingTip
    };

    // Non-three finger joints (little only)
    readonly XRHandJointID[] nonThreeFingerTips = {
        XRHandJointID.LittleTip
    };

    // ─────────────────────── Unity lifecycle ───────────────────────
    void Start()
    {
        // Find a running XRHandSubsystem
        var subsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        subsystem = subsystems.Find(s => s.running);

        if (subsystem == null)
        {
            Debug.LogError("[LeftHandGestureControl] No running XRHandSubsystem found.");
            enabled = false;
            return;
        }

        subsystem.updatedHands += OnHandsUpdated;
        SetStatusText("<color=white>[Teleop] Stopped</color>");
        
        // Initialize ROS connection and publisher
        InitializeROS();
    }

    void OnDestroy()
    {
        if (subsystem != null)
            subsystem.updatedHands -= OnHandsUpdated;
    }

    // ─────────────────────── ROS Initialization ───────────────────────
    void InitializeROS()
    {
        // Get ROS connection instance
        ros = ROSConnection.GetOrCreateInstance();
        
        // Register publisher for pause state topic
        ros.RegisterPublisher<BoolMsg>(pauseTopicName);
        
        // Register publisher for calibration trigger topic
        ros.RegisterPublisher<EmptyMsg>(calibrationTopicName);
        
        // Register publisher for go home trigger topic
        ros.RegisterPublisher<EmptyMsg>(goHomeTopicName);
        
        // Publish initial state
        PublishPauseState();
        
        Debug.Log($"[LeftHandGestureControl] ROS publishers initialized for topics: {pauseTopicName}, {calibrationTopicName}, {goHomeTopicName}");
    }

    // ─────────────────────── Hand-tracking callback ───────────────────────
    void OnHandsUpdated(XRHandSubsystem s,
                        XRHandSubsystem.UpdateSuccessFlags flags,
                        XRHandSubsystem.UpdateType type)
    {
        // Only process dynamic updates to reduce frequency (optimization)
        if (type != XRHandSubsystem.UpdateType.Dynamic) return;
        
        // Prevent toggling during the post-switch lock window
        if (Time.time < lockUntil) return;

        XRHand left = s.leftHand;
        if (!left.isTracked)
        {
            // Lost tracking ▸ reset timers, keep state
            fistTimer = openTimer = swordFingerTimer = threeFingerTimer = 0f;

            string textColor = isPaused ? "white" : "green";
            string status = isPaused ? "Paused" : "Running";
            SetStatusText($"<color={textColor}>[Teleop] {status}</color>");
            return;
        }

        bool fist = IsFist(left);
        bool open = IsOpen(left);
        bool swordFinger = IsSwordFinger(left);
        bool threeFinger = IsThreeFinger(left);

        // Priority: Three finger > Sword finger > Fist > Open hand
        if (threeFinger && Time.time >= goHomeCooldownUntil)
        {
            threeFingerTimer += Time.deltaTime;
            fistTimer = openTimer = swordFingerTimer = 0f;

            SetStatusText("<color=orange>[Teleop] Confirming Go Home...</color>");

            if (threeFingerTimer >= goHomeHoldDuration)
                TriggerGoHome();
        }
        else if (swordFinger && Time.time >= calibrationCooldownUntil)
        {
            swordFingerTimer += Time.deltaTime;
            fistTimer = openTimer = threeFingerTimer = 0f;

            SetStatusText("<color=#00FFFF>[Teleop] Confirming Calibration...</color>");

            if (swordFingerTimer >= calibrationHoldDuration)
                TriggerCalibration();
        }
        else if (fist)
        {
            fistTimer += Time.deltaTime;
            openTimer = swordFingerTimer = threeFingerTimer = 0f;

            // Early feedback during confirmation
            if (!isPaused)
                SetStatusText("<color=yellow>[Teleop] Confirming Pause...</color>");

            if (!isPaused && fistTimer >= holdDuration)
                SwitchPausedState(true);
        }
        else if (open)
        {
            openTimer += Time.deltaTime;
            fistTimer = swordFingerTimer = threeFingerTimer = 0f;

            if (isPaused)
                SetStatusText("<color=yellow>[Teleop] Confirming Resume...</color>");

            if (isPaused && openTimer >= holdDuration)
                SwitchPausedState(false);
        }
        else
        {
            // No relevant gesture
            fistTimer = openTimer = swordFingerTimer = threeFingerTimer = 0f;
            
            // Show status with priority: Go Home Triggered > Calibration Triggered > Go Home Cooldown > Calibration Cooldown > Normal
            if (Time.time < goHomeTriggeredUntil)
            {
                // Keep showing "Go Home Triggered" message
                SetStatusText("<color=orange>[Teleop] Go Home Triggered</color>");
            }
            else if (Time.time < calibrationTriggeredUntil)
            {
                // Keep showing "Calibration Triggered" message
                SetStatusText("<color=#00FFFF>[Teleop] Calibration Triggered</color>");
            }
            else if (Time.time < goHomeCooldownUntil)
            {
                float remainingCooldown = goHomeCooldownUntil - Time.time;
                SetStatusText($"<color=orange>[Teleop] Go Home Cooldown ({remainingCooldown:F1}s)</color>");
            }
            else if (Time.time < calibrationCooldownUntil)
            {
                float remainingCooldown = calibrationCooldownUntil - Time.time;
                SetStatusText($"<color=#00FFFF>[Teleop] Calibration Cooldown ({remainingCooldown:F1}s)</color>");
            }
            else
            {
                string textColor = isPaused ? "white" : "green";
                string status = isPaused ? "Paused" : "Running";
                SetStatusText($"<color={textColor}>[Teleop] {status}</color>");
            }
        }
    }

    // ─────────────────────── Gesture helpers ───────────────────────
    bool IsFist(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist)) return false;

        // Cancel thumb check since it's not stable
        // Thumb must be close to wrist
        // if (!TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb) ||
        //     Vector3.Distance(thumb, wrist) > thumbCurlThreshold)
        //     return false;

        // All four fingers must be close to wrist
        foreach (var tip in fingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos) ||
                Vector3.Distance(pos, wrist) > fingerCurlThreshold)
                return false;
        }
        return true;
    }

    bool IsOpen(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist)) return false;

        // Check distance between hand and headset to prevent false detection when hand is lowered
        if (!IsHandNearHeadset(wrist)) return false;

        // Thumb must be clearly away from wrist
        // if (!TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb) ||
        //     Vector3.Distance(thumb, wrist) < thumbExtendThreshold)
        //     return false;

        // At least three of four fingers extended
        int extended = 0;
        foreach (var tip in fingerTips)
        {
            if (TryJointPos(hand, tip, out Vector3 pos) &&
                Vector3.Distance(pos, wrist) > fingerExtendThreshold)
                extended++;
        }
        return extended >= 4;
    }

    bool IsSwordFinger(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist)) return false;

        // Check distance between hand and headset to prevent false detection when hand is lowered
        if (!IsHandNearHeadset(wrist)) return false;

        // Index and middle fingers must be extended
        foreach (var tip in swordFingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos) ||
                Vector3.Distance(pos, wrist) < fingerExtendThreshold)
                return false;
        }

        // Ring and little fingers must be curled
        foreach (var tip in nonSwordFingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos) ||
                Vector3.Distance(pos, wrist) > fingerCurlThreshold)
                return false;
        }

        // Thumb should be curled (optional check, can be commented out if unstable)
        // if (TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb) &&
        //     Vector3.Distance(thumb, wrist) > thumbCurlThreshold)
        //     return false;

        return true;
    }

    bool IsThreeFinger(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist)) return false;

        // Check distance between hand and headset to prevent false detection when hand is lowered
        if (!IsHandNearHeadset(wrist)) return false;

        // Index, middle, and ring fingers must be extended
        foreach (var tip in threeFingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos) ||
                Vector3.Distance(pos, wrist) < fingerExtendThreshold)
                return false;
        }

        // Little finger must be curled
        foreach (var tip in nonThreeFingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos) ||
                Vector3.Distance(pos, wrist) > fingerCurlThreshold)
                return false;
        }

        // Thumb should be curled (optional check, can be commented out if unstable)
        // if (TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb) &&
        //     Vector3.Distance(thumb, wrist) > thumbCurlThreshold)
        //     return false;

        return true;
    }

    bool TryJointPos(XRHand hand, XRHandJointID id, out Vector3 pos)
    {
        pos = Vector3.zero;
        var joint = hand.GetJoint(id);
        if (!joint.TryGetPose(out Pose pose)) return false;
        pos = pose.position;
        return true;
    }

    // Debug version of IsFist - shows distance calculations and threshold comparisons
    bool IsFistDebug(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist))
        {
            Debug.Log("<color=red>[IsFistDebug] Failed to get wrist position</color>");
            return false;
        }

        Debug.Log($"[IsFistDebug] Wrist position: {wrist}");

        // Check thumb distance to wrist
        if (!TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb))
        {
            Debug.Log("<color=red>[IsFistDebug] Failed to get thumb tip position</color>");
            return false;
        }
        
        float thumbDistance = Vector3.Distance(thumb, wrist);
        bool thumbCurled = thumbDistance <= thumbCurlThreshold;
        string thumbColor = thumbCurled ? "green" : "red";
        Debug.Log($"<color={thumbColor}>[IsFistDebug] Thumb distance: {thumbDistance:F4}m (threshold: <={thumbCurlThreshold:F4}m) - {(thumbCurled ? "CURLED" : "EXTENDED")}</color>");
        
        // if (!thumbCurled) 
        // {
        //     Debug.Log("<color=red>[IsFistDebug] Final result: NOT A FIST (thumb not curled)</color>");
        //     return false;
        // }

        // Check all four finger distances to wrist
        bool allFingersCurled = true;
        foreach (var tip in fingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos))
            {
                Debug.Log($"<color=red>[IsFistDebug] Failed to get {tip} position</color>");
                return false;
            }
            
            float fingerDistance = Vector3.Distance(pos, wrist);
            bool fingerCurled = fingerDistance <= fingerCurlThreshold;
            string fingerColor = fingerCurled ? "green" : "red";
            Debug.Log($"<color={fingerColor}>[IsFistDebug] {tip} distance: {fingerDistance:F4}m (threshold: <={fingerCurlThreshold:F4}m) - {(fingerCurled ? "CURLED" : "EXTENDED")}</color>");
            
            if (!fingerCurled)
                allFingersCurled = false;
        }
        
        bool result = allFingersCurled && thumbCurled;
        string resultColor = result ? "green" : "red";
        Debug.Log($"<color={resultColor}>[IsFistDebug] Final result: {(result ? "FIST DETECTED" : "NOT A FIST")}</color>");
        
        return result;
    }

    // Debug version of IsOpen - shows distance calculations and threshold comparisons
    bool IsOpenDebug(XRHand hand)
    {
        if (!TryJointPos(hand, XRHandJointID.Wrist, out Vector3 wrist))
        {
            Debug.Log("<color=red>[IsOpenDebug] Failed to get wrist position</color>");
            return false;
        }

        Debug.Log($"[IsOpenDebug] Wrist position: {wrist}");

        // Check thumb must be extended (far from wrist)
        if (!TryJointPos(hand, XRHandJointID.ThumbTip, out Vector3 thumb))
        {
            Debug.Log("<color=red>[IsOpenDebug] Failed to get thumb tip position</color>");
            return false;
        }
        
        float thumbDistance = Vector3.Distance(thumb, wrist);
        bool thumbExtended = thumbDistance >= thumbExtendThreshold;
        string thumbColor = thumbExtended ? "green" : "red";
        Debug.Log($"<color={thumbColor}>[IsOpenDebug] Thumb distance: {thumbDistance:F4}m (threshold: >={thumbExtendThreshold:F4}m) - {(thumbExtended ? "EXTENDED" : "CURLED")}</color>");
        
        if (!thumbExtended) 
        {
            Debug.Log("<color=red>[IsOpenDebug] Final result: NOT OPEN HAND (thumb not extended)</color>");
            return false;
        }

        // Check fingers - at least 3 of 4 must be extended
        int extendedCount = 0;
        foreach (var tip in fingerTips)
        {
            if (!TryJointPos(hand, tip, out Vector3 pos))
            {
                Debug.Log($"<color=orange>[IsOpenDebug] Failed to get {tip} position - skipping</color>");
                continue; // Skip this finger but don't fail completely
            }
            
            float fingerDistance = Vector3.Distance(pos, wrist);
            bool fingerExtended = fingerDistance >= fingerExtendThreshold;
            string fingerColor = fingerExtended ? "green" : "red";
            Debug.Log($"<color={fingerColor}>[IsOpenDebug] {tip} distance: {fingerDistance:F4}m (threshold: >={fingerExtendThreshold:F4}m) - {(fingerExtended ? "EXTENDED" : "CURLED")}</color>");
            
            if (fingerExtended)
                extendedCount++;
        }
        
        bool result = extendedCount >= 4;
        string resultColor = result ? "green" : "red";
        Debug.Log($"<color={resultColor}>[IsOpenDebug] Extended fingers: {extendedCount}/4 (need >=3) - Final result: {(result ? "OPEN HAND DETECTED" : "NOT OPEN HAND")}</color>");
        
        return result;
    }

    // ─────────────────────── Distance helper ───────────────────────
    bool IsHandNearHeadset(Vector3 handPosition)
    {
        // Get headset position from main camera (VR headset)
        if (Camera.main == null)
        {
            Debug.LogWarning("[LeftHandGestureControl] Main camera not found for distance check");
            return true; // Allow detection if we can't get headset position
        }

        Vector3 headsetPosition = Camera.main.transform.position;
        float distance = Vector3.Distance(handPosition, headsetPosition);
        
        bool isNear = distance <= maxHandHeadsetDistance;
        
        // Optional debug logging (can be commented out for performance)
        // Debug.Log($"[LeftHandGestureControl] Hand-headset distance: {distance:F3}m (threshold: {maxHandHeadsetDistance:F3}m) - {(isNear ? "NEAR" : "FAR")}");
        
        return isNear;
    }

    // ─────────────────────── State switching ───────────────────────
    void SwitchPausedState(bool pause)
    {
        isPaused  = pause;
        lockUntil = Time.time + postSwitchLock;

        if (pause)
        {
            Debug.Log("<color=white>[Teleop] Paused</color>");
            SetStatusText("<color=white>[Teleop] Paused</color>");
            // TODO: Call your teleop-pause logic or publish ROS message here
        }
        else
        {
            Debug.Log("<color=green>[Teleop] Resumed</color>");
            SetStatusText("<color=green>[Teleop] Running</color>");
            // TODO: Call your teleop-resume logic or publish ROS message here
        }
        
        // Publish pause state change
        PublishPauseState();
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
            
            Debug.Log($"[LeftHandGestureControl] Published pause state: {isPaused} to {pauseTopicName}");
        }
    }

    // ─────────────────────── Calibration trigger ───────────────────────
    void TriggerCalibration()
    {
        swordFingerTimer = 0f;
        calibrationTriggeredUntil = Time.time + calibrationConfirmDuration;
        calibrationCooldownUntil = Time.time + calibrationCooldown;

        Debug.Log("<color=#00FFFF>[Teleop] Calibration Triggered</color>");
        SetStatusText("<color=#00FFFF>[Teleop] Calibration Triggered</color>");

        // Publish calibration trigger via ROS
        if (ros != null)
        {
            ros.Publish(calibrationTopicName, new EmptyMsg());
            Debug.Log($"[LeftHandGestureControl] Published calibration trigger to {calibrationTopicName}");
        }
    }

    // ─────────────────────── Go Home trigger ───────────────────────
    void TriggerGoHome()
    {
        threeFingerTimer = 0f;
        goHomeTriggeredUntil = Time.time + goHomeConfirmDuration;
        goHomeCooldownUntil = Time.time + goHomeCooldown;

        Debug.Log("<color=orange>[Teleop] Go Home Triggered</color>");
        SetStatusText("<color=orange>[Teleop] Go Home Triggered</color>");

        // Publish go home trigger via ROS
        if (ros != null)
        {
            ros.Publish(goHomeTopicName, new EmptyMsg());
            Debug.Log($"[LeftHandGestureControl] Published go home trigger to {goHomeTopicName}");
        }
    }

    // ─────────────────────── UI helper ───────────────────────
    void SetStatusText(string txt)
    {
        if (statusText != null)
            statusText.text = txt;
    }
} 