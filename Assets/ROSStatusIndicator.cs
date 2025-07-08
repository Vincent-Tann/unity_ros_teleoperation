using TMPro;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;

public class ROSStatusIndicator : MonoBehaviour
{
    ROSConnection ros;
    TextMeshProUGUI txt;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        txt = GetComponent<TextMeshProUGUI>();
        Debug.Log($"IP={ros.RosIPAddress} Port={ros.RosPort}");

    }

    void Update()
    {
        // 1) Have not started connection thread
        if (!ros.HasConnectionThread)
        {
            txt.text = $"<color=grey>ROS: Waiting…</color>\n"
                     + $"{ros.RosIPAddress}:{ros.RosPort}";
            return;
        }

        // 2) Connection thread is running, but has not connected yet
        if (ros.HasConnectionError)
        {
            txt.text = $"<color=red>ROS DISCONNECTED</color>\n"
                     + $"{ros.RosIPAddress}:{ros.RosPort}";
            return;
        }

        // 3) Connection thread is running and has connected
        txt.text = $"<color=green>ROS CONNECTED</color>\n"
                 + $"{ros.RosIPAddress}:{ros.RosPort}";
    }
}
