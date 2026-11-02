using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace AgentConsole.View
{
    /// <summary>
    /// Orbit camera with a lock.
    ///  - Unlocked: free orbit / zoom / pan. The graph never moves itself and neither does the rig.
    ///  - Locked: auto-frames the active call. Port of the prototype's focus(): centre on the MIDPOINT of
    ///    the agent and the lit node, with the distance set so the whole path stays in frame. Centring on
    ///    the node alone would push the agent off-screen. Orbiting is still allowed while locked.
    /// Every transition is eased (frame-rate independent exponential smoothing), never snapped.
    ///
    /// The rig keeps its state as a VIEW POSE in graph-local space (pivot, yaw, pitch, distance) and only
    /// at the very end decides what to move. <see cref="MoveMode.Camera"/> moves the camera around the
    /// graph; <see cref="MoveMode.Graph"/> leaves the camera where it is and moves the graph root so the
    /// same view results. XR will want the second: the headset camera is not ours to move.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public enum MoveMode { Camera, Graph }

        [SerializeField] private Camera viewCamera;
        [SerializeField] private Transform graphRoot;
        [SerializeField] private MoveMode moveMode = MoveMode.Camera;
        [SerializeField] private bool startLocked = true;

        [Header("Easing")]
        [Tooltip("Higher = snappier. Used for pivot and distance.")]
        [SerializeField, Min(0.1f)] private float easeK = 2.6f;
        [SerializeField, Min(0.1f)] private float orbitEaseK = 12f;

        [Header("Input")]
        [SerializeField] private float orbitDegreesPerPixel = 0.22f;
        [SerializeField] private float zoomStep = 0.12f;
        [SerializeField] private float panPerPixel = 0.0016f;
        [SerializeField] private float minDistance = 3f;
        [SerializeField] private float maxDistance = 90f;

        [Header("Framing")]
        [SerializeField] private float fitMargin = 1.15f;
        [SerializeField] private float pathPadding = 1.6f;
        [Tooltip("Smallest frame half-extent, as a fraction of the graph's bounding radius. Used when focusing the agent alone.")]
        [SerializeField, Range(0.2f, 1f)] private float minFrameFraction = 0.9f;
        [SerializeField] private float startYaw = 28f;
        [SerializeField] private float startPitch = 16f;

        private GraphView _graph;
        private Vector3 _pivot, _pivotT;
        private float _dist = 30f, _distT = 30f;
        private float _yaw, _yawT, _pitch, _pitchT;
        private bool _locked;
        private bool _dragging;
        private bool _panning;
        private bool _hasPath;
        private Vector3 _pathA, _pathB;
        private float _pathR;
        private Vector3 _homePos;
        private Quaternion _homeRot;

        public bool Locked => _locked;
        public MoveMode Mode => moveMode;
        public event Action<bool> LockChanged;

        private void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (graphRoot == null)
            {
                GraphView gv = FindAnyObjectByType<GraphView>();
                if (gv != null) graphRoot = gv.transform;
            }
            _homePos = viewCamera.transform.position;
            _homeRot = viewCamera.transform.rotation;
            _yaw = _yawT = startYaw;
            _pitch = _pitchT = startPitch;
            _locked = startLocked;
        }

        public void Bind(GraphView graph)
        {
            if (_graph != null)
            {
                _graph.PathFocused -= OnPath;
                _graph.FitRequested -= OnFit;
            }
            _graph = graph;
            _graph.PathFocused += OnPath;
            _graph.FitRequested += OnFit;
            OnFit();
            // Start already framed rather than sweeping in from an arbitrary distance.
            _pivot = _pivotT;
            _dist = _distT;
            LockChanged?.Invoke(_locked);
        }

        private void OnDestroy()
        {
            if (_graph != null)
            {
                _graph.PathFocused -= OnPath;
                _graph.FitRequested -= OnFit;
            }
        }

        public void SetLocked(bool locked)
        {
            if (_locked == locked) return;
            _locked = locked;
            if (_locked) ReapplyFocus();
            LockChanged?.Invoke(_locked);
        }

        public void ToggleLock() => SetLocked(!_locked);

        /// <summary>Frame the whole graph (eased). Always remembered; only applied while locked.</summary>
        public void Fit()
        {
            OnFit();
            if (!_locked) ApplyFit();
        }

        private void OnFit()
        {
            _hasPath = false;
            if (_locked) ApplyFit();
        }

        private void OnPath(Vector3 agent, Vector3 node, float radius)
        {
            _hasPath = true;
            _pathA = agent;
            _pathB = node;
            _pathR = radius;
            if (_locked) ApplyPath();
        }

        private void ReapplyFocus()
        {
            if (_hasPath) ApplyPath();
            else ApplyFit();
        }

        private void ApplyFit()
        {
            _pivotT = Vector3.zero;
            _distT = FrameDistance(_graph != null ? _graph.BoundingRadius : 10f) * fitMargin;
        }

        private void ApplyPath()
        {
            // The midpoint of agent and node, not the node: centring on the node alone pushes the other end off screen.
            _pivotT = (_pathA + _pathB) * 0.5f;
            float bound = _graph != null ? _graph.BoundingRadius : 10f;
            float half = Mathf.Max((_pathB - _pathA).magnitude * 0.5f + _pathR + pathPadding, bound * minFrameFraction);
            _distT = FrameDistance(half);
        }

        /// <summary>Distance at which a sphere of this radius fits the narrower of the two FOV axes.</summary>
        private float FrameDistance(float radius)
        {
            float vHalf = viewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float hHalf = Mathf.Atan(Mathf.Tan(vHalf) * viewCamera.aspect);
            float half = Mathf.Min(vHalf, hHalf);
            return Mathf.Clamp(radius / Mathf.Sin(half), minDistance, maxDistance);
        }

        // ------------------------------------------------------------------ per frame

        private void Update()
        {
            ReadInput();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float k = 1f - Mathf.Exp(-easeK * dt);
            float ko = 1f - Mathf.Exp(-orbitEaseK * dt);
            _pivot = Vector3.Lerp(_pivot, _pivotT, k);
            _dist = Mathf.Lerp(_dist, _distT, k);
            _yaw = Mathf.Lerp(_yaw, _yawT, ko);
            _pitch = Mathf.Lerp(_pitch, _pitchT, ko);
        }

        private void LateUpdate()
        {
            ApplyPose();
        }

        private static bool PointerOverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void ReadInput()
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame && !ConsoleUI.IsTyping)
            {
                ToggleLock();
            }

            Mouse m = Mouse.current;
            if (m == null) return;

            if (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame)
            {
                _dragging = !PointerOverUi();
            }
            if (m.middleButton.wasPressedThisFrame)
            {
                _panning = !PointerOverUi();
            }
            if (!m.leftButton.isPressed && !m.rightButton.isPressed) _dragging = false;
            if (!m.middleButton.isPressed) _panning = false;

            Vector2 delta = m.delta.ReadValue();
            if (_dragging)
            {
                _yawT += delta.x * orbitDegreesPerPixel;
                _pitchT = Mathf.Clamp(_pitchT - delta.y * orbitDegreesPerPixel, -85f, 85f);
            }

            if (_locked) return; // the lock owns pivot and distance

            if (_panning)
            {
                Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
                Vector3 shift = (rot * Vector3.right) * (-delta.x) + (rot * Vector3.up) * (-delta.y);
                _pivotT += shift * (_dist * panPerPixel);
            }

            float scroll = m.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && !PointerOverUi())
            {
                _distT = Mathf.Clamp(_distT * Mathf.Exp(-Mathf.Sign(scroll) * zoomStep), minDistance, maxDistance);
            }
        }

        private void ApplyPose()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 camLocal = _pivot - rot * Vector3.forward * _dist;
            Transform root = graphRoot != null ? graphRoot : transform;
            Transform cam = viewCamera.transform;

            if (moveMode == MoveMode.Camera)
            {
                cam.SetPositionAndRotation(root.TransformPoint(camLocal), root.rotation * rot);
            }
            else
            {
                // Same view, other way round: leave the camera on its home pose and carry the graph.
                Quaternion graphRot = _homeRot * Quaternion.Inverse(rot);
                root.SetPositionAndRotation(_homePos - graphRot * camLocal, graphRot);
                cam.SetPositionAndRotation(_homePos, _homeRot);
            }
        }
    }
}
