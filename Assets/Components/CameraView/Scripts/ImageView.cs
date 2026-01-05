using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RosMessageTypes.Std;
using RosMessageTypes.Sensor;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine.UI;
using System.Threading.Tasks;
using TMPro;
using UnityEngine.Experimental.Rendering;



#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(ImageView))]
public class ImageViewEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        ImageView imageView = (ImageView)target;
        if (GUILayout.Button("Click"))
        {
            imageView.OnClick();
        }
        if (GUILayout.Button("Select First Item"))
        {
            imageView.OnSelect(1);
        }
        
        if (GUILayout.Button("Select Second Item"))
        {
            imageView.OnSelect(2);
        }
        if (GUILayout.Button("Clear"))
        {
            imageView.OnSelect(0);
        }
        if (GUILayout.Button("Render"))
        {
            imageView.Render();
        }
    }
}
#endif

[System.Serializable]
public struct ImageData
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    public string topicName;
    public int trackingState;
    public bool flip;
    public bool stereo;
}

[System.Serializable]
public class ImageView : MonoBehaviour
{
    public Dropdown dropdown;
    public GameObject topMenu;
    public CameraManager manager;
    public TMPro.TextMeshProUGUI name;
    public Sprite untracked;
    public Sprite tracked;
    public Sprite headTracked;
    public ComputeShader debayer;
    public Material material;

    public string topicName;

    private RenderTexture _texture2D;
    protected Transform _Img;

    protected int _lastSelected = 0;

    protected GameObject _frustrum;
    protected Image _icon;
    protected ROSConnection ros;

    public int _trackingState = 0;
    protected Sprite[] icons;

    public enum DebayerMode
    {
        RGGB,
        BGGR,
        GBRG,
        GRBG,
        None=-1,
    }

    public DebayerMode debayerType = DebayerMode.GRBG;


    public void OnValidate()
    {
        if (debayer != null && debayerType != DebayerMode.None)
        {
            debayer.SetInt("mode", (int)debayerType);
        }
    }

    public bool CleanTF(string name)
    {
        GameObject target = GameObject.Find(name);

        if(target == null)
        {
            return false;
        }

        List<GameObject> children = new List<GameObject>();

        // check if this is connected to root
        int count = 0;
        while(target.transform.parent != null)
        {
            count++;
            children.Add(target);
            target = target.transform.parent.gameObject;
            if(target.name == "odom")
            {
                children.Clear();
                Debug.Log("Connected to root");
                return true;
            }
            if(count > 100)
            {
                Debug.LogError("Looping too much");
                return false;
            }
        }

        foreach(GameObject child in children)
        {
            Destroy(child);
        }
        return false;
    }

    void UpdatePose(string frame)
    {
        if(!CleanTF(frame))
        {
            return;
        }
        GameObject _parent = GameObject.Find(frame);
        if(_parent == null) return;

        transform.parent = _parent.transform;
        transform.localPosition = new Vector3(0.1f, 0.2f, 0);
        transform.localRotation = Quaternion.Euler(-90, 90, 180);
        // transform.localScale = new Vector3(-1, 1, 1);
    }

    void Awake()
    {
        try
        {
            // Setup name text
            if (name != null)
            {
        name.text = "None";
                Debug.Log("[ImageView] Name text set to 'None'");
            }
            else
            {
                Debug.LogWarning("[ImageView] Name text component is null");
            }

            // Setup icons array
        icons = new Sprite[] {untracked, tracked, headTracked};
            Debug.Log("[ImageView] Icons array initialized");

            // Find and setup the track icon
            if (topMenu != null)
            {
                var trackImageTransform = topMenu.transform.Find("Track/Image/Image");
                if (trackImageTransform != null)
                {
                    _icon = trackImageTransform.GetComponent<Image>();
                    if (_icon != null)
                    {
                        Debug.Log("[ImageView] Track icon found and assigned");
                    }
                    else
                    {
                        Debug.LogWarning("[ImageView] Track icon Image component not found");
                    }
                }
                else
                {
                    Debug.LogWarning("[ImageView] Track/Image/Image path not found in topMenu");
                }
            }
            else
            {
                Debug.LogWarning("[ImageView] TopMenu is null in Awake - icon will be assigned later");
            }
            
            Debug.Log("[ImageView] Awake() completed");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception in Awake(): {e.Message}");
            Debug.LogError($"[ImageView] Exception stack trace: {e.StackTrace}");
        }
    }

    void Start()
    {
        try
        {
            // Find and setup the Img component
        _Img = transform.Find("Img");
            if (_Img == null)
            {
                Debug.LogError("[ImageView] Could not find 'Img' child object");
                return;
            }
            
            var meshRenderer = _Img.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
            {
                Debug.LogError("[ImageView] 'Img' object does not have a MeshRenderer component");
                return;
            }
            
            material = meshRenderer.material;
            if (material == null)
            {
                Debug.LogError("[ImageView] MeshRenderer does not have a material");
                return;
            }
            
            Debug.Log("[ImageView] Successfully found Img component and material");

            // Setup dropdown
            if (dropdown != null)
            {
        dropdown.onValueChanged.AddListener(OnSelect);
                dropdown.gameObject.SetActive(false);
                Debug.Log("[ImageView] Dropdown setup successful");
            }
            else
            {
                Debug.LogError("[ImageView] Dropdown is null - please assign it in the inspector");
            }

            // Setup top menu
            if (topMenu != null)
            {
        topMenu.SetActive(false);
                Debug.Log("[ImageView] TopMenu setup successful");
            }
            else
            {
                Debug.LogError("[ImageView] TopMenu is null - please assign it in the inspector");
            }

            // Initialize ROS connection
            InitializeROS();

            // Find frustrum
            Transform frustrumTransform = transform.Find("Frustrum");
            if (frustrumTransform != null)
            {
                _frustrum = frustrumTransform.gameObject;
                Debug.Log("[ImageView] Successfully found Frustrum component");
            }
            else
            {
                Debug.LogWarning("[ImageView] Could not find 'Frustrum' child object - this is optional");
            }
            
            Debug.Log("[ImageView] Start() completed successfully");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception in Start(): {e.Message}");
            Debug.LogError($"[ImageView] Exception stack trace: {e.StackTrace}");
        }
    }
    
    void InitializeROS()
    {
        try
        {
            // Get ROS connection instance
            ros = ROSConnection.GetOrCreateInstance();
            
            if (ros == null)
            {
                Debug.LogError("[ImageView] Failed to get ROS connection instance");
                return;
            }
            
            Debug.Log($"[ImageView] ROS connection instance obtained successfully");
            
            // Perform comprehensive ROS connection diagnostics
            PerformROSDiagnostics();
            
            // Request topic list with delay to ensure connection is ready
            StartCoroutine(DelayedTopicRequest());
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception during ROS initialization: {e.Message}");
        }
    }
    
    void PerformROSDiagnostics()
    {
        try
        {
            Debug.Log("[ImageView] ===== ROS CONNECTION DIAGNOSTICS =====");
            
            // Check ROS connection state
            if (ros != null)
            {
                Debug.Log("[ImageView] ✓ ROS connection instance is not null");
                
                // Try to get basic ROS connection info by reflection if available
                var rosType = ros.GetType();
                Debug.Log($"[ImageView] ROS connection type: {rosType.Name}");
                
                // Try to find connection properties
                var properties = rosType.GetProperties();
                foreach (var prop in properties)
                {
                    if (prop.Name.Contains("IP") || prop.Name.Contains("Port") || prop.Name.Contains("Url") || prop.Name.Contains("Address"))
                    {
                        try
                        {
                            var value = prop.GetValue(ros);
                            Debug.Log($"[ImageView] ROS {prop.Name}: {value}");
                        }
                        catch
                        {
                            Debug.Log($"[ImageView] ROS {prop.Name}: <unable to read>");
                        }
                    }
                }
            }
            else
            {
                Debug.LogError("[ImageView] ✗ ROS connection instance is null");
            }
            
            Debug.Log("[ImageView] ===== END ROS DIAGNOSTICS =====");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception during ROS diagnostics: {e.Message}");
        }
    }
    
    private System.Collections.IEnumerator DelayedTopicRequest()
    {
        // Wait a bit for ROS connection to fully establish
        yield return new WaitForSeconds(0.5f);
        
        Debug.Log("[ImageView] Requesting topic list from ROS...");
        
        // Test direct subscription to see if ROS connection works for subscribing
        TestDirectSubscription();
        
        // Try GetTopicAndTypeList with error handling
        bool exceptionOccurred = false;
        System.Exception caughtException = null;
        
        try
        {            
            // Attempt to send topic list request
            ros.GetTopicAndTypeList((topics) => {
                Debug.Log($"[ImageView] GetTopicAndTypeList callback received with {topics.Count} topics");
                UpdateTopics(topics);
            });
            
            Debug.Log("[ImageView] GetTopicAndTypeList request sent, waiting for response...");
        }
        catch (System.Exception e)
        {
            exceptionOccurred = true;
            caughtException = e;
        }
        
        // Handle exception outside of try block
        if (exceptionOccurred)
        {
            Debug.LogError($"[ImageView] Exception calling GetTopicAndTypeList: {caughtException.Message}");
            Debug.LogError("[ImageView] Using fallback predefined topics due to exception");
            
            Dictionary<string, string> emptyTopics = new Dictionary<string, string>();
            UpdateTopics(emptyTopics);
            yield break;
        }
        
        // Wait longer for ROS Master response
        yield return new WaitForSeconds(2.0f);
        
        // Check if we got any response
        if (dropdown != null && dropdown.options.Count <= 1)
        {
            Debug.LogWarning("[ImageView] GetTopicAndTypeList didn't return any image topics or failed completely");
            Debug.LogWarning("[ImageView] This is common when ROS Master is not running or not accessible");
            Debug.LogWarning("[ImageView] Using fallback predefined topics instead");
            
            // Force use predefined topics as fallback
            Dictionary<string, string> emptyTopics = new Dictionary<string, string>();
            UpdateTopics(emptyTopics);
        }
    }
    
    private void TestDirectSubscription()
    {
        try
        {
            // Test if we can subscribe to our known topic
            string testTopic = "/camera2/image_raw";
            Debug.Log($"[ImageView] Testing direct subscription to {testTopic}...");
            
            // This will help us understand if the issue is with GetTopicAndTypeList or with subscribing
            ros.Subscribe<ImageMsg>(testTopic, TestImageCallback);
            Debug.Log($"[ImageView] Direct subscription test successful for {testTopic}");
            
            // Unsubscribe immediately after test
            ros.Unsubscribe(testTopic);
            Debug.Log($"[ImageView] Test subscription cleaned up for {testTopic}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Test subscription failed: {e.Message}");
        }
    }
    
    private void TestImageCallback(ImageMsg msg)
    {
        Debug.Log($"[ImageView] TEST CALLBACK: Received image data! Size: {msg.width}x{msg.height}, Data: {msg.data.Length} bytes");
        // This is just for testing - we'll unsubscribe immediately after subscription
    }

    private void OnDestroy() {
        ros.Unsubscribe(topicName);
    }

    protected virtual void UpdateTopics(Dictionary<string, string> topics)
    {
        Debug.Log($"[ImageView] UpdateTopics called with {topics.Count} topics from ROS");
        
        List<string> options = new List<string>();
        options.Add("None");
        
        // Add predefined topics first (always available)
        List<string> predefinedTopics = new List<string>
        {
            "/camera2/image_raw",
            "/camera4/image_raw"
        };
        
        foreach (string predefinedTopic in predefinedTopics)
        {
            if (!options.Contains(predefinedTopic))
            {
                options.Add(predefinedTopic);
                Debug.Log($"[ImageView] Added predefined topic: {predefinedTopic}");
            }
        }
        
        // Add discovered topics from ROS
        foreach (var topic in topics)
        {
            Debug.Log($"[ImageView] Discovered ROS Topic: {topic.Key} - Type: {topic.Value}");
            if (topic.Value == "sensor_msgs/Image" || topic.Value == "sensor_msgs/CompressedImage")
            {
                // issue with depth images at the moment
                if (topic.Key.Contains("depth")) continue;
                
                // Avoid duplicates with predefined topics
                if (!options.Contains(topic.Key))
                {
                options.Add(topic.Key);
                    Debug.Log($"[ImageView] Added ROS discovered topic: {topic.Key}");
                }
            }
        }

        Debug.Log($"[ImageView] Total options available: {options.Count} - [{string.Join(", ", options)}]");

        if(options.Count == 1)
        {
            Debug.LogWarning("[ImageView] No image topics found!");
            return;
        }
        
        if (dropdown != null)
        {
        dropdown.ClearOptions();
            dropdown.AddOptions(options);
            dropdown.value = Mathf.Min(_lastSelected, options.Count - 1);
            Debug.Log($"[ImageView] Dropdown updated with {options.Count} options successfully");
        }
        else
        {
            Debug.LogError("[ImageView] Dropdown is null - cannot update options!");
        }
    }
    
    // Force subscribe to a test topic for debugging
    public void ForceSubscribeToTestTopic()
    {
        string testTopic = "/camera2/image_raw";
        Debug.Log($"[ImageView] FORCE SUBSCRIBING to test topic: {testTopic}");
        
        if (ros != null)
        {
            try
            {
                topicName = testTopic;
                ros.Subscribe<ImageMsg>(testTopic, OnImage);
                Debug.Log($"[ImageView] Successfully force subscribed to {testTopic}");
                
                if (name != null)
                    name.text = testTopic;
                    
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ImageView] Failed to force subscribe to {testTopic}: {e.Message}");
            }
        }
        else
        {
            Debug.LogError("[ImageView] Cannot force subscribe - ROS connection is null");
        }
    }
    
    // // Debug methods for testing tracking modes in Inspector
    // [Header("Debug Controls")]
    // [Space(10)]
    public void SetTrackingMode0_FixedWorld()
    {
        Debug.Log("[ImageView] DEBUG: Setting to Fixed World Mode (0)");
        ToggleTrack(0);
    }
    
    public void SetTrackingMode1_TFTracking()
    {
        Debug.Log("[ImageView] DEBUG: Setting to TF Tracking Mode (1)");
        ToggleTrack(1);
    }
    
    public void SetTrackingMode2_HeadTracking()
    {
        Debug.Log("[ImageView] DEBUG: Setting to Head Tracking Mode (2)");
        ToggleTrack(2);
    }
    
    public void DebugPrintCurrentState()
    {
        Debug.Log($"[ImageView] === CURRENT STATE DEBUG ===");
        Debug.Log($"[ImageView] Tracking State: {_trackingState}");
        Debug.Log($"[ImageView] Parent: {(transform.parent != null ? transform.parent.name : "null")}");
        Debug.Log($"[ImageView] Frustrum: {(_frustrum != null ? (_frustrum.activeSelf ? "Active" : "Inactive") : "null")}");
        Debug.Log($"[ImageView] Position: {transform.position}");
        Debug.Log($"[ImageView] === END STATE DEBUG ===");
    }

    public void Clear()
    {
        manager.Remove(gameObject);
    }

    public void ToggleTrack(int newState)
    {
        int oldState = _trackingState;
        _trackingState = newState % 3;

        Debug.Log($"[ImageView] ToggleTrack: {oldState} -> {_trackingState}");
        
        // Update the icon if available
        if (_icon != null && icons != null && _trackingState < icons.Length)
        {
            _icon.sprite = icons[_trackingState];
            Debug.Log($"[ImageView] Updated tracking icon for state: {_trackingState}");
        }
        
        // Force immediate state change regardless of image data
        ForceTrackingStateChange();
    }

    public void ToggleTrack()
    {
        ToggleTrack(_trackingState + 1);
        dropdown.gameObject.SetActive(false);
        topMenu.SetActive(false);
    }
    
    // Force tracking state change without waiting for image data
    public void ForceTrackingStateChange()
    {
        Debug.Log($"[ImageView] Force applying tracking state: {_trackingState}");
        
        if (_trackingState == 0)
        {
            // Fixed World Position Mode
            if (_frustrum != null) _frustrum.SetActive(false);
            
            if (transform.parent != null && transform.parent.name != "odom")
            {
                Vector3 worldPos = transform.position;
                Quaternion worldRot = transform.rotation;
                UpdatePose("odom");
                transform.position = worldPos;
                transform.rotation = worldRot;
            }
            Debug.Log("[ImageView] Forced to Fixed World Mode");
        }
        else if (_trackingState == 1)
        {
            // TF Tracking Mode - will be updated when image data arrives
            Debug.Log("[ImageView] Set to TF Tracking Mode - waiting for image data to determine frame");
        }
        else if (_trackingState == 2)
        {
            // Head Tracking Mode
            if (_frustrum != null) _frustrum.SetActive(false);
            
            if (Camera.main != null)
            {
                transform.parent = Camera.main.transform;
                Debug.Log("[ImageView] Forced to Head Tracking Mode");
            }
            else
            {
                Debug.LogError("[ImageView] Cannot switch to Head Tracking - Camera.main is null");
            }
        }
    }

    public void Flip()
    {
    }

    public void ScaleUp()
    {
        transform.localScale *= 1.1f;
    }

    public void ScaleDown()
    {
        transform.localScale *= 0.9f;
    }

    public void OnClick()
    {
        Debug.Log("[ImageView] OnClick called - refreshing topic list from ROS");
        
        if (ros != null)
    {
        ros.GetTopicAndTypeList(UpdateTopics);
        }
        else
        {
            Debug.LogWarning("[ImageView] ROS connection is null, using predefined topics only");
            Dictionary<string, string> emptyTopics = new Dictionary<string, string>();
            UpdateTopics(emptyTopics);
        }
        
        dropdown.gameObject.SetActive(!dropdown.gameObject.activeSelf);
        topMenu.gameObject.SetActive(dropdown.gameObject.activeSelf);
        Debug.Log($"[ImageView] Dropdown active: {dropdown.gameObject.activeSelf}");
    }

    public virtual void OnSelect(int value)
    {
        if (value == _lastSelected) return;

        _lastSelected = value;
        
        Debug.Log($"[ImageView] OnSelect called with value: {value}");

        if (topicName != null)
        {
            Debug.Log($"[ImageView] Unsubscribing from previous topic: {topicName}");
            ros.Unsubscribe(topicName);
        }

        name.text = dropdown.options[value].text;
        Debug.Log($"[ImageView] Selected option text: {name.text}");

        if (value == 0)
        {
            topicName = null;
            // set texture to grey
            material.SetTexture("_BaseMap", null);
            
            dropdown.gameObject.SetActive(false);
            topMenu.SetActive(false);
            Debug.Log("[ImageView] Selected 'None' - cleared topic and texture");
            return;
        }

        topicName = dropdown.options[value].text;
        Debug.Log($"[ImageView] Attempting to subscribe to topic: {topicName}");

        if (ros == null)
        {
            Debug.LogError("[ImageView] ROS connection is null! Cannot subscribe to topic.");
            return;
        }

        try
        {
            if (topicName.EndsWith("compressed"))
            {
                Debug.Log($"[ImageView] Subscribing to compressed image topic: {topicName}");
            ros.Subscribe<CompressedImageMsg>(topicName, OnCompressed);
        }
        else
        {
                Debug.Log($"[ImageView] Subscribing to raw image topic: {topicName}");
            ros.Subscribe<ImageMsg>(topicName, OnImage);
            }
            Debug.Log($"[ImageView] Successfully subscribed to topic: {topicName}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception while subscribing to topic {topicName}: {e.Message}");
        }
        
        dropdown.gameObject.SetActive(false);
        topMenu.SetActive(false);
    }

    protected virtual void SetupTex(int width = 2, int height = 2)
    {
        if (_texture2D == null || _texture2D.width != width || _texture2D.height != height)
        {
            if (_texture2D != null)
            {
                _texture2D.Release();
                Debug.Log($"[ImageView] Released old texture for topic: {topicName}");
            }
            
            _texture2D = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_UNorm);
            _texture2D.enableRandomWrite = true;
            _texture2D.Create();
            material.SetTexture("_BaseMap", _texture2D);
            Debug.Log($"[ImageView] Created new texture {width}x{height} for topic: {topicName}");
        }
    }

    /// <summary>
    /// For debugging, render the current image to a file
    /// </summary>
    public void Render()
    {
        // Save the _uiImage rendertexture to a file
        RenderTexture.active = _texture2D;
        Texture2D tex = new Texture2D(_texture2D.width, _texture2D.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, _texture2D.width, _texture2D.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        byte[] bytes = tex.EncodeToPNG();
        
        string filename = name.text.Replace("/", "_");
        System.IO.File.WriteAllBytes(Application.dataPath + "/../" + filename + ".png", bytes);
    }

    protected virtual void Resize()
    {
        if (_texture2D == null) return;
        float aspectRatio = (float)_texture2D.width/(float)_texture2D.height;

        float width = _Img.transform.localScale.x;
        float height = width / aspectRatio;
        
        _Img.localScale = new Vector3(width, 1, height);
    }

    protected void ParseHeader(HeaderMsg header)
    {
        Debug.Log($"[ImageView] ParseHeader called with tracking state: {_trackingState}, frame_id: {header.frame_id}");
        
        if (_trackingState == 1)
        {
            // TF Tracking Mode: Follow ROS coordinate frame with frustrum
            if(header.frame_id != null && (transform.parent == null || header.frame_id != transform.parent.name))
            {
                if (_frustrum != null)
            {
                _frustrum.SetActive(true);
                    Debug.Log($"[ImageView] TF Tracking: Frustrum activated for frame: {header.frame_id}");
                }
                else
                {
                    Debug.LogWarning("[ImageView] TF Tracking: Frustrum object is null - cannot show camera frustrum");
                }
                // If the parent is not the same as the frame_id, update the parent
                UpdatePose(header.frame_id);
            }

        } else if (_trackingState == 0)
        {
            // Fixed World Position Mode: Stay in world coordinates
            if (_frustrum != null)
        {
            _frustrum.SetActive(false);
            }
            
            if (transform.parent != null && transform.parent.name != "odom")
            {
                // Move to odom frame but keep current world position
                Vector3 worldPos = transform.position;
                Quaternion worldRot = transform.rotation;
            UpdatePose("odom");
                transform.position = worldPos;
                transform.rotation = worldRot;
                Debug.Log("[ImageView] Fixed World Mode: Moved to odom frame, preserving world position");
            }
        } else if (_trackingState == 2)
        {
            // Head Tracking Mode: Follow the headset
            if (_frustrum != null)
            {
                _frustrum.SetActive(false);
            }
            
            if (transform.parent != Camera.main.transform)
            {
            transform.parent = Camera.main.transform;
                Debug.Log("[ImageView] Head Tracking Mode: Attached to camera");
            }
        }
    }

    void OnCompressed(CompressedImageMsg msg)
    {
        Debug.Log($"[ImageView] OnCompressed called! Topic: {topicName}, Data size: {msg.data.Length} bytes, Header: {msg.header.frame_id}");
        
        ParseHeader(msg.header);

        try
        {
            Texture2D _input = new Texture2D(2, 2);
            bool loaded = ImageConversion.LoadImage(_input, msg.data);
            
            if (!loaded)
            {
                Debug.LogError($"[ImageView] Failed to load compressed image data for topic: {topicName}");
                Destroy(_input);
                return;
            }
            
            _input.Apply();
            Debug.Log($"[ImageView] Successfully loaded compressed image: {_input.width}x{_input.height} for topic: {topicName}");
            
            SetupTex(_input.width, _input.height);

            if(debayerType == DebayerMode.None)
            {
                RenderTexture.active = _texture2D;
                Graphics.Blit(_input, _texture2D);
                RenderTexture.active = null;
                Debug.Log($"[ImageView] Applied compressed image to texture (no debayer) for topic: {topicName}");
                Destroy(_input);
                Resize();
                return;
            }

            // debayer the image using compute shader
            debayer.SetInt("mode", (int)debayerType);
            debayer.SetTexture(0, "Input", _input);
            debayer.SetTexture(0, "Result", _texture2D);
            debayer.Dispatch(0, _input.width / 2, _input.height / 2, 1);

            Debug.Log($"[ImageView] Applied compressed image to texture with debayer mode {debayerType} for topic: {topicName}");
            Destroy(_input);
            Resize();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception in OnCompressed for topic {topicName}: {e.Message}");
            Debug.LogError($"[ImageView] Exception stack trace: {e.StackTrace}");
        }
    }

    void OnImage(ImageMsg msg)
    {
        Debug.Log($"[ImageView] OnImage called! Topic: {topicName}, Size: {msg.width}x{msg.height}, Data size: {msg.data.Length} bytes, Encoding: {msg.encoding}, Header: {msg.header.frame_id}");
        
        SetupTex((int)msg.width, (int)msg.height);
        ParseHeader(msg.header);

        try
        {
            // Process raw image data - this was commented out in original code!
            Debug.Log($"[ImageView] Processing raw image data for topic: {topicName}");
            
            Texture2D inputTexture = null;
            
            if (msg.encoding == "rgb8")
            {
                // For RGB 8-bit images
                inputTexture = new Texture2D((int)msg.width, (int)msg.height, TextureFormat.RGB24, false);
                inputTexture.LoadRawTextureData(msg.data);
                inputTexture.Apply();
                Debug.Log($"[ImageView] Successfully loaded raw RGB8 image for topic: {topicName}");
            }
            else if (msg.encoding == "bgr8")
            {
                // For BGR 8-bit images - need to swap R and B channels
                byte[] rgbData = new byte[msg.data.Length];
                for (int i = 0; i < msg.data.Length; i += 3)
                {
                    rgbData[i] = msg.data[i + 2];     // R = B
                    rgbData[i + 1] = msg.data[i + 1]; // G = G
                    rgbData[i + 2] = msg.data[i];     // B = R
                }
                inputTexture = new Texture2D((int)msg.width, (int)msg.height, TextureFormat.RGB24, false);
                inputTexture.LoadRawTextureData(rgbData);
                inputTexture.Apply();
                Debug.Log($"[ImageView] Successfully loaded raw BGR8 image (converted to RGB) for topic: {topicName}");
            }
            else if (msg.encoding == "mono8")
            {
                // For grayscale images
                inputTexture = new Texture2D((int)msg.width, (int)msg.height, TextureFormat.R8, false);
                inputTexture.LoadRawTextureData(msg.data);
                inputTexture.Apply();
                Debug.Log($"[ImageView] Successfully loaded mono8 image for topic: {topicName}");
            }
            else
            {
                Debug.LogWarning($"[ImageView] Unsupported encoding '{msg.encoding}' for topic: {topicName}. Trying RGB24 format...");
                inputTexture = new Texture2D((int)msg.width, (int)msg.height, TextureFormat.RGB24, false);
                inputTexture.LoadRawTextureData(msg.data);
                inputTexture.Apply();
            }
            
            if (inputTexture != null)
            {
                // Copy the Texture2D to RenderTexture
                RenderTexture.active = _texture2D;
                Graphics.Blit(inputTexture, _texture2D);
                RenderTexture.active = null;
                
                Debug.Log($"[ImageView] Successfully applied raw {msg.encoding} image to render texture for topic: {topicName}");
                
                // Clean up temporary texture
                Destroy(inputTexture);
                
                Resize();
            }
            else
            {
                Debug.LogError($"[ImageView] Failed to create input texture for topic: {topicName}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ImageView] Exception in OnImage for topic {topicName}: {e.Message}");
            Debug.LogError($"[ImageView] Exception stack trace: {e.StackTrace}");
        }
    }

    public virtual void Deserialize(string data)
    {
        try{
            ImageData imgData = JsonUtility.FromJson<ImageData>(data);
            
            transform.position = imgData.position;
            transform.rotation = imgData.rotation;
            transform.localScale = imgData.scale;
            topicName = imgData.topicName;
            _trackingState = imgData.trackingState;

            if (topicName.EndsWith("compressed"))
            {
                ros.Subscribe<CompressedImageMsg>(topicName, OnCompressed);
                name.text = topicName;
            }
            else if (topicName != null)
            {
                ros.Subscribe<ImageMsg>(topicName, OnImage);
                name.text = topicName;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError(e);
            Debug.LogError("Error deserializing image data! Most likely old data format, clearing prefs");
            PlayerPrefs.DeleteKey("layout");
            PlayerPrefs.Save();
        }
    }

    public virtual string Serialize()
    {
        ImageData data = new ImageData();
        data.position = transform.position;
        data.rotation = transform.rotation;
        data.scale = transform.localScale;
        data.topicName = topicName;
        data.trackingState = _trackingState;
        data.flip = false;
        data.stereo = false;

        return JsonUtility.ToJson(data);
    }
}
