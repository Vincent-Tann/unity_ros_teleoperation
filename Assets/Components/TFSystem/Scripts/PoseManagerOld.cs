using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.Events;

#if UNITY_EDITOR
using UnityEditor;

[CustomEditor(typeof(PoseManagerOld))]
public class PoseManagerOldEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        PoseManagerOld myScript = (PoseManagerOld)target;
        if (GUILayout.Button("Center robot"))
        {
            myScript.BaseToLocation(Vector3.zero);
        }
        if (GUILayout.Button("Toggle Fixed Location"))
        {
            myScript.ToggleFixedLocation();
        }
        if (GUILayout.Button("Lock"))
        {
            myScript.SetLocked(true);
        }
        if (GUILayout.Button("Unlock"))
        {
            myScript.SetLocked(false);
        }
    }
}
#endif

public class PoseManagerOld : MonoBehaviour
{
    public static PoseManagerOld Instance { get; private set; }

    public InputActionReference joystickXY;
    public InputActionReference joystickZR;
    public InputActionReference alt;
    public InputActionReference action;
    public InputActionReference bAction;

    public UnityEvent actions;
    public UnityEvent bActions;

    public GameObject sphere;
    public GameObject floor;
    public float speed = 1.0f;
    public float rotationTolerance = 5.0f;
    public bool handSelectable = false;
    public Transform root;
    public Transform _root;
    private Transform _mainCamera;
    private Transform _robot;

    public bool _locked = false;
    private Vector3 _center;
    private Vector3 _forward;

    void Awake()
    {
        // if (Instance != null && Instance != this)
        //     Destroy(this);
        // else
        //     Instance = this;
        Instance = this;
    }

    public static PoseManagerOld GetOrCreateInstance()
    {
        // if (Instance == null)
        // {
        //     GameObject go = new GameObject();
        //     go.name = "PoseManager";
        //     return go.AddComponent<PoseManager>();
        // }
        return Instance;
    }

    void Start()
    {
        if (root == null)
        {
            root = transform;
        }
        // need to ensure this happens after tf init....
        _mainCamera = Camera.main.transform;

        if (actions != null && action == null)
        {
            actions = null;
            Debug.LogWarning("PoseManager: actions set but action not set");
        }

        GameObject robot = GameObject.FindWithTag("robot");

        if (_robot == null)
            Debug.LogWarning("PoseManager: robot not found");        
        else
            _robot = robot.transform;

    }

    void Update()
    {
        if(_robot == null)
        {
            GameObject robot = GameObject.FindWithTag("robot");
            if (robot == null)
            {
                Debug.LogWarning("PoseManager: robot not found");
            }
            else
            {
                _robot = robot.transform;
            }
        }

        if (root != null)
        {
            while (root.parent != null)
            {
                root = root.parent;
                _root = root;
                Debug.Log("root frame: " + root);
            }
        }

        if(_center != Vector3.zero)
        {
            BaseToLocation(_center);
        }

        if (action?.action != null && action.action.IsPressed())
        {
            if (alt?.action != null && alt.action.IsPressed())
            {
                ResetScale();
            }
            else
            {
                actions?.Invoke();
            }
        }

        if(bAction?.action != null && bAction.action.IsPressed())
        {
            bActions?.Invoke();
        }

        if(_locked)
            return;
            
        bool joystickPressed = false;
        if (joystickXY?.action != null && joystickXY.action.IsPressed())
            joystickPressed = true;
        if (joystickZR?.action != null && joystickZR.action.IsPressed())
            joystickPressed = true;
            
        if (joystickPressed)
        {
            if (alt?.action != null && alt.action.IsPressed())
            {
                if (joystickXY?.action != null)
                    Scale(joystickXY.action.ReadValue<Vector2>());
            }
            else
            {
                if (joystickXY?.action != null)
                    Move(joystickXY.action.ReadValue<Vector2>());
            }

            if (joystickZR?.action != null)
                OffsetRotate(joystickZR.action.ReadValue<Vector2>());

            if (sphere != null)
                sphere.SetActive(true);
        }
        else
        {
            if (sphere != null)
                sphere.SetActive(false);
        }
        
        if(floor != null && _root != null)
        {
            floor.transform.position = new Vector3(_root.position.x, 0, _root.position.z);
        }
    }

    public void SetLocked(bool locked)
    {
        Debug.Log(locked);
        _locked = locked;
    }

    void ResetScale()
    {
        _root.localScale = Vector3.one;
    }

    public void ToggleFixedLocation()
    {
        if (_robot == null) return;
        
        if (_center == Vector3.zero)
        {
            _center = _robot.position;
            _forward = _robot.forward;
        }
        else
        {
            _center = Vector3.zero;
            _forward = Vector3.zero;
        }
    }

    void Move(Vector2 input)
    {
        Vector3 move = new Vector3(input.x, 0, input.y);
        // move = root.TransformDirection(move);

        // get relative to the player's view point
        if (_mainCamera != null)
        {
            move = _mainCamera.TransformDirection(move);
        }
        // take into account the gameobject's orientation
        // zero out the vertical component
        move.y = 0;
        // move the gameobject relative to the player regardless of gameobject orientation
        if (_root != null)
        {
            _root.Translate(move * speed * Time.deltaTime, Space.World);
        }
    }

    void Scale(Vector2 input)
    {
        if (_root == null) return;
        
        Vector3 scale = new Vector3(input.x, input.x, input.x);
        scale = Vector3.Scale(_root.localScale, scale);
        _root.localScale += scale * speed * Time.deltaTime;
    }

    void OffsetRotate(Vector2 input)
    {
        if (_root == null) return;
        
        // offset on the y axis based on forwards/back on second joystick
        Vector3 move = new Vector3(0, input.y, 0);

        // rotate on the x axis based on left/right on second joystick
        _root.Translate(move * speed * Time.deltaTime / 10);
        _root.Rotate(0, input.x * speed * Time.deltaTime * 20, 0);
    }

    public void ClickCb(SelectEnterEventArgs args)
    {
        if (_root == null || !handSelectable) return;

        Vector3 position;
        XRRayInteractor rayInteractor = (XRRayInteractor)args.interactor;
        rayInteractor.TryGetHitInfo(out position, out _, out _, out _);
        _root.position = position;
    }

    public void BaseToLocation(Vector3 position)
    {
        if (_robot == null || _root == null) return;
        
        Vector3 offset = position - _robot.position;
        _root.position += offset;

        float angle = Vector3.SignedAngle(_robot.forward, _forward, Vector3.up);
        if (_forward != Vector3.zero && Mathf.Abs(angle) > rotationTolerance)
        {
            _root.Rotate(0, angle, 0);
        }
    }
}
