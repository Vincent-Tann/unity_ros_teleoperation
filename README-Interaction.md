# VR Teleoperation Interaction System

This document outlines the interaction logic and ROS interfaces for the Unity VR teleoperation system.

## Overview

The system provides multiple interaction modalities for controlling a robotic teleoperation setup:
- **Pose tracking** for headset and hand positions
- **Hand keypoint publishing** for gripper control
- **Gesture/button control** for system functions (pause/resume, calibration, go-home)

## Components

### 1. HeadsetPublisherNew.cs
**Purpose**: Publishes VR headset and hand poses to ROS

**Input Sources**:
- XR Input System (headset and controller tracking)
- Time synchronization from ROS master

**Published Topics**:
```
/Quest3/pose/headset        (geometry_msgs/PoseStamped)
/Quest3/pose/hand_left      (geometry_msgs/PoseStamped) 
/Quest3/pose/hand_right     (geometry_msgs/PoseStamped)
/tf                         (tf2_msgs/TFMessage) [optional]
```

**Key Features**:
- Time synchronization with ROS master via `/ros_time_header`
- Unity→ROS coordinate transformation (FLU)
- Optional TF publishing (controlled by `send_tf` parameter)
- Frame names: `vr_origin` → `headset`, `hand_left`, `hand_right`

**Configuration**:
- `send_tf`: Enable/disable TF publishing
- `rosTimeTopic`: Topic for ROS time synchronization

---

### 2. HandPubWorld.cs
**Purpose**: Publishes right hand joint positions for gripper control

**Input Sources**:
- XR Hand Tracking API (21 hand joints)
- Optional controller input for gesture triggers

**Published Topics**:
```
/Quest3/hand_points         (sensor_msgs/PointCloud)
/Quest3/hand_gesture        (retargeting_ros/HandGesture)
```

**Key Features**:
- Maps 21 OpenXR hand joints to ROS point cloud
- World coordinate system publishing
- Thumb-index finger distance → gripper open/close mapping
- High/low confidence tracking modes
- Y-up to Z-up coordinate swizzling for ROS

**Joint Mapping**:
```
0: Wrist           11: MiddleDistal      
1: ThumbMetacarpal 12: MiddleTip
2: ThumbProximal   13: RingProximal
3: ThumbDistal     14: RingIntermediate
4: ThumbTip        15: RingDistal
5: IndexProximal   16: RingTip
6: IndexIntermediate 17: LittleProximal
7: IndexDistal     18: LittleIntermediate
8: IndexTip        19: LittleDistal
9: MiddleProximal  20: LittleTip
10: MiddleIntermediate
```

---

### 3. LeftHandGestureControl.cs
**Purpose**: Hand tracking-based system control

**Input Sources**:
- XR Hand Tracking API (left hand)
- Gesture recognition algorithms

**Published Topics**:
```
/Quest3/isPaused            (std_msgs/Bool)
/Quest3/requireCalibration  (std_msgs/Empty)
/Quest3/requireGoHome       (std_msgs/Empty)
```

**Gesture Commands**:
| Gesture | Hold Duration | Function |
|---------|---------------|----------|
| ✊ Fist | 1.0s | Pause teleoperation |
| ✋ Open hand | 1.0s | Resume teleoperation |
| ✌️ Sword finger (index+middle) | 0.5s | Trigger calibration |
| Three fingers (index+middle+ring) | 0.8s | Trigger go-home |

**Key Features**:
- Distance-based gesture recognition
- Hand-headset proximity filtering (40cm max)
- Temporal locks to prevent rapid toggling
- Cooldown periods for calibration (3s) and go-home (3s)
- Real-time status feedback via TextMeshPro UI

---

### 4. LeftHandControllerControl.cs
**Purpose**: Controller button-based system control (alternative to gestures)

**Input Sources**:
- XR Input System (left controller buttons)
- Device connect/disconnect events

**Published Topics**:
```
/Quest3/isPaused            (std_msgs/Bool)
/Quest3/requireCalibration  (std_msgs/Empty)  
/Quest3/requireGoHome       (std_msgs/Empty)
```

**Button Mapping**:
| Button | Function | Cooldown |
|--------|----------|----------|
| 🎯 Trigger | Toggle pause/resume | 0.3s |
| 🅰️ A button | Trigger calibration | 3s |
| 🅱️ B button | Trigger go-home | 3s |

**Key Features**:
- Edge detection for button presses (no holding required)
- Dynamic controller connection/disconnection support
- Same ROS topics as gesture control (seamless switching)
- Independent enable/disable for each button function

---

### 5. HapticFeedbackReceiver.cs
**Purpose**: Receives haptic feedback commands from ROS and applies them to VR controllers

**Input Sources**:
- ROS topics for left/right hand haptic commands
- XR Input System (controller device management)
- Device connect/disconnect events

**Subscribed Topics**:
```
/Quest3/haptics/left         (std_msgs/Float32MultiArray)
/Quest3/haptics/right        (std_msgs/Float32MultiArray)
```

**Message Format**:
```
Float32MultiArray.data[0] = amplitude (0.0-1.0)  // Vibration intensity
Float32MultiArray.data[1] = duration (seconds)   // Vibration duration
```

**Key Features**:
- Dual controller haptic support (left/right independent)
- Dynamic controller connection/disconnection handling
- Automatic device reinitialization on tracking loss
- Haptic capability validation before sending impulses
- Log throttling to prevent console spam (2s cooldown)
- Graceful fallback when controllers unavailable

**Usage Examples**:
```bash
# Trigger right controller vibration (max intensity, 1 second)
rostopic pub -1 /Quest3/haptics/right std_msgs/Float32MultiArray "data: [1.0, 1.0]"

# Gentle left controller pulse (50% intensity, 0.3 seconds)
rostopic pub -1 /Quest3/haptics/left std_msgs/Float32MultiArray "data: [0.5, 0.3]"

# Short strong feedback (100% intensity, 0.1 seconds)
rostopic pub -1 /Quest3/haptics/right std_msgs/Float32MultiArray "data: [1.0, 0.1]"
```

**Controller Compatibility**:
- Works only when controllers are active (not in hand tracking mode)
- Automatically detects Quest Touch controllers
- Channel 0 targets the main haptic motor
- Requires `HapticCapabilities.supportsImpulse` = true

---

## System Integration

### Coordinate Systems
- **Unity**: Left-handed, Y-up
- **ROS**: Right-handed, Z-up (FLU transformation applied)
- **Hand tracking**: World coordinates for gripper precision

### Interaction Modes
The system supports seamless switching between:
1. **Hand tracking mode**: Gesture control + hand keypoints
2. **Controller mode**: Button control + controller poses
3. **Mixed mode**: Both systems active simultaneously

### Status Display
All components provide real-time status via TextMeshPro UI:
```
[Teleop] Running/Paused
[Controller] Running/Paused/Not Connected
[Teleop] Confirming Pause/Resume/Calibration/Go Home
[Controller] Calibration/Go Home Triggered/Cooldown
```

### Error Handling
- Graceful fallback when tracking is lost
- Device reconnection support
- Log throttling to prevent console spam
- Exception-safe ROS callbacks

## Usage Examples

### ROS Subscribers (Linux side)
```bash
# Monitor teleoperation state
rostopic echo /Quest3/isPaused

# Listen for calibration requests
rostopic echo /Quest3/requireCalibration

# Monitor hand positions for gripper control
rostopic echo /Quest3/hand_points

# Track headset pose for robot base positioning
rostopic echo /Quest3/pose/headset

# Test haptic feedback
rostopic pub -1 /Quest3/haptics/right std_msgs/Float32MultiArray "data: [1.0, 1.0]"
```

### Gripper Control Logic (ROS side)
```python
# Example: Extract thumb-index distance for gripper
def hand_callback(msg):
    thumb_tip = msg.points[4]    # Index 4: ThumbTip
    index_tip = msg.points[8]    # Index 8: IndexTip
    distance = calculate_distance(thumb_tip, index_tip)
    gripper_openness = normalize_distance(distance)
    publish_gripper_command(gripper_openness)

# Example: Send haptic feedback on gripper action
def send_haptic_feedback(hand, intensity, duration):
    topic = "/Quest3/haptics/left" if hand == "left" else "/Quest3/haptics/right"
    haptic_msg = Float32MultiArray()
    haptic_msg.data = [intensity, duration]
    haptic_pub.publish(haptic_msg)
```

### System Commands
```bash
# Trigger calibration via ROS (alternative to gestures/buttons)
rostopic pub -1 /Quest3/requireCalibration std_msgs/Empty "{}"

# Set pause state
rostopic pub -1 /Quest3/isPaused std_msgs/Bool "data: true"

# Send haptic confirmation feedback
rostopic pub -1 /Quest3/haptics/left std_msgs/Float32MultiArray "data: [0.8, 0.2]"
```

## Configuration Notes

1. **Time Synchronization**: Ensure `/ros_time_header` is published from ROS master
2. **Frame Configuration**: Adjust frame IDs in HeadsetPublisherNew for your TF tree
3. **Gesture Sensitivity**: Tune threshold distances in LeftHandGestureControl
4. **UI Integration**: Connect statusText fields to TextMeshPro components in scene
5. **Performance**: Use high-confidence mode in HandPubWorld for production accuracy
6. **Haptic Feedback**: Test controller haptic capabilities before deployment

## Troubleshooting

- **No hand tracking**: Check XRHandSubsystem is running and hands are visible
- **Controller not detected**: Verify Quest controllers are paired and charged
- **ROS connection issues**: Check ROSConnection instance and topic registration
- **Coordinate misalignment**: Verify FLU transformation and world frame setup
- **Gesture not triggering**: Adjust threshold distances and check hand-headset proximity
- **Haptic feedback not working**: Ensure controllers are active (not in hand tracking mode) and check haptic capabilities