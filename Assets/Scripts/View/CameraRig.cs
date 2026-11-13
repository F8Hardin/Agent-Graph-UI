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
        [Tooltip("Distance easing while the wheel is turning. Higher = the zoom follows the wheel more tightly.")]
        [SerializeField, Min(0.1f)] private float zoomEaseK = 9f;

        [Header("Input")]
        [SerializeField] private float orbitDegreesPerPixel = 0.22f;
        [SerializeField] private float zoomStep = 0.12f;
        [SerializeField] private float panPerPixel = 0.0016f;
        [SerializeField] private float minDistance = 3f;
        [SerializeField] private float maxDistance = 90f;
        [Tooltip("WASD speed, in view distances per second, so it feels the same zoomed in or out.")]
        [SerializeField, Min(0.05f)] private float flySpeed = 0.6f;

        [Header("Framing")]
        [SerializeField] private float fitMargin = 1.15f;
        [SerializeField] private float pathPadding = 1.6f;
        [Tooltip("Room left around a double-clicked node and its children, as a multiple of their radius.")]
        [SerializeField, Min(1f)] private float focusMargin = 1.5f;
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
        private bool _nodeDrag;
        private Vector2 _lastPointer;
        private float _frameDist = 30f;   // the lock's own framing distance, before the user's zoom
        private float _zoomMul = 1f;      // the wheel's multiplier on top of it while locked
        private float _zoomUntil;
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
                _graph.NodeFocusRequested -= OnNodeFocus;
            }
            _graph = graph;
            _graph.PathFocused += OnPath;
            _graph.FitRequested += OnFit;
            _graph.NodeFocusRequested += OnNodeFocus;
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
                _graph.NodeFocusRequested -= OnNodeFocus;
            }
        }

public void SetLocked(bool locked)
        {
            if (_locked == locked) return;
            _locked = locked;
            if (_locked)
            {
                _zoomMul = 1f; // locking again means "frame it for me": start from the lock's own distance
                ReapplyFocus();
            }
            LockChanged?.Invoke(_locked);
        }

        public void ToggleLock() => SetLocked(!_locked);

        /// <summary>Frame the whole graph (eased). Always remembered; only applied while locked.</summary>
        public void Fit()
        {
            _zoomMul = 1f; // the Fit button is an explicit "reset the view"; a run ending (OnFit) keeps the user's zoom
            OnFit();
            if (!_locked) ApplyFit();
        }

        private void OnFit()
        {
            _hasPath = false;
            // Never re-frame while a node is held: the frame moving under a still pointer is half of the runaway
            // described on GraphView.DragBy. The release re-applies whatever was asked for meanwhile.
            if (_locked && !_nodeDrag) ApplyFit();
        }

        private void OnPath(Vector3 agent, Vector3 node, float radius)
        {
            _hasPath = true;
            _pathA = agent;
            _pathB = node;
            _pathR = radius;
            if (_locked && !_nodeDrag) ApplyPath();
        }

        /// <summary>
        /// A double-click: centre the node and frame it with its children. Works locked or not. Locked, the next tool
        /// call or Fit re-frames as usual, which is what the lock means; unlocked, the view stays here.
        /// </summary>
        private void OnNodeFocus(Vector3 centre, float radius)
        {
            _pivotT = centre;
            _zoomMul = 1f;
            _frameDist = FrameDistance(Mathf.Max(radius, 0.5f) * focusMargin);
            _distT = Mathf.Clamp(_frameDist, minDistance, maxDistance);
        }

        private void ReapplyFocus()
        {
            if (_hasPath) ApplyPath();
            else ApplyFit();
        }

        private void ApplyFit()
        {
            // Round the agent, not the origin: the agent can be dragged away and the graph comes with it.
            _pivotT = _graph != null ? _graph.GraphCenter : Vector3.zero;
            _frameDist = FrameDistance(_graph != null ? _graph.BoundingRadius : 10f) * fitMargin;
            _distT = Mathf.Clamp(_frameDist * _zoomMul, minDistance, maxDistance);
        }

        private void ApplyPath()
        {
            // The midpoint of agent and node, not the node: centring on the node alone pushes the other end off screen.
            _pivotT = (_pathA + _pathB) * 0.5f;
            float bound = _graph != null ? _graph.BoundingRadius : 10f;
            float half = Mathf.Max((_pathB - _pathA).magnitude * 0.5f + _pathR + pathPadding, bound * minFrameFraction);
            _frameDist = FrameDistance(half);
            _distT = Mathf.Clamp(_frameDist * _zoomMul, minDistance, maxDistance);
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
            float kz = Time.unscaledTime < _zoomUntil ? 1f - Mathf.Exp(-Mathf.Max(easeK, zoomEaseK) * dt) : k;
            float ko = 1f - Mathf.Exp(-orbitEaseK * dt);
            _pivot = Vector3.Lerp(_pivot, _pivotT, k);
            _dist = Mathf.Lerp(_dist, _distT, kz);
            _yaw = Mathf.Lerp(_yaw, _yawT, ko);
            _pitch = Mathf.Lerp(_pitch, _pitchT, ko);
        }

        private void LateUpdate()
        {
            ApplyPose();
        }

        /// <summary>
        /// Pointer arbitration, in the one place that already tells a node drag from an orbit. A press belongs to the
        /// UI when the EventSystem says so, and also when it lands on an island or an island is being grabbed: the
        /// island rect registry covers the frame where the island's own press handler has not run yet, and Grabbing
        /// keeps a drag that slides off the island from turning into a camera orbit.
        /// </summary>
        private static bool PointerOverUi()
        {
            if (IslandRegistry.Grabbing) return true;
            Mouse m = Mouse.current;
            if (m != null && IslandRegistry.PointerOver(m.position.ReadValue())) return true;
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void ReadInput()
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame && !ConsoleUI.IsTyping)
            {
                ToggleLock();
            }

            // WASD flies the view: W/S along the view direction, A/D sideways, Space up, Shift down. Not while typing, and not
            // with Ctrl held (Ctrl+A, Ctrl+S and friends are not movement). Pressing them takes the camera off the lock,
            // because the lock owns the pivot and would pull the view straight back; L or the Lock button re-locks.
            // Pivot and target move together so the view moves rigidly instead of easing behind the keys.
            Vector3 fly = FlyStep(kb);
            if (fly != Vector3.zero)
            {
                if (_locked) SetLocked(false);
                // Space also means "press the selected button" to the UI. While flying, nothing should be selected, or
                // going up would click whatever button was used last.
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null) es.SetSelectedGameObject(null);
                _pivotT += fly;
                _pivot += fly;
            }

            Mouse m = Mouse.current;
            if (m == null) return;

            // Who owns a press: a left press on a sphere grabs that node; anywhere else it orbits. A press that
            // lands on the UI belongs to the UI. The graph does the picking, the rig only arbitrates.
            if (m.leftButton.wasPressedThisFrame)
            {
                bool overUi = PointerOverUi();
                if (!overUi && _graph != null && _graph.BeginDrag(PointerRay(m)))
                {
                    _nodeDrag = true;
                    _dragging = false;
                    _lastPointer = m.position.ReadValue();
                }
                else
                {
                    _dragging = !overUi;
                }
            }
            else if (m.rightButton.wasPressedThisFrame && !_nodeDrag)
            {
                _dragging = !PointerOverUi();
            }
            if (m.middleButton.wasPressedThisFrame)
            {
                _panning = !PointerOverUi();
            }

            if (_nodeDrag)
            {
                if (m.leftButton.isPressed)
                {
                    // The node copies the pointer: the pointer's movement in pixels becomes the same movement in the
                    // camera's plane at the node's depth. WASD carries the node with the view, so holding a node and
                    // flying moves it through the scene with the camera following.
                    Vector2 p = m.position.ReadValue();
                    Vector2 d = p - _lastPointer;
                    _lastPointer = p;
                    Transform cam = viewCamera.transform;
                    float depth = Mathf.Max(0.1f, Vector3.Dot(_graph.DragTargetWorld - cam.position, cam.forward));
                    float worldPerPixel = 2f * depth * Mathf.Tan(viewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                    Vector3 move = (cam.right * d.x + cam.up * d.y) * worldPerPixel;
                    if (fly != Vector3.zero)
                    {
                        Transform root = graphRoot != null ? graphRoot : transform;
                        move += root.TransformVector(fly);
                    }
                    if (move != Vector3.zero) _graph.DragBy(move);
                }
                else
                {
                    _graph.EndDrag();
                    _nodeDrag = false;
                    if (_locked) ReapplyFocus(); // frame what moved, now that nothing is held
                }
            }
            if (_nodeDrag || IslandRegistry.Grabbing || (!m.leftButton.isPressed && !m.rightButton.isPressed)) _dragging = false;
            if (!m.middleButton.isPressed || IslandRegistry.Grabbing) _panning = false;

            Vector2 delta = m.delta.ReadValue();
            if (_dragging)
            {
                _yawT += delta.x * orbitDegreesPerPixel;
                _pitchT = Mathf.Clamp(_pitchT - delta.y * orbitDegreesPerPixel, -85f, 85f);
            }

            // The wheel works locked and unlocked. Locked, it scales the lock's own distance rather than
            // replacing it, so the next auto-frame keeps the user's zoom instead of snapping it away.
            float scroll = m.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && !_nodeDrag && !PointerOverUi())
            {
                ZoomBy(scroll);
            }

            if (_locked) return; // the lock owns the pivot

            if (_panning)
            {
                Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
                Vector3 shift = (rot * Vector3.right) * (-delta.x) + (rot * Vector3.up) * (-delta.y);
                _pivotT += shift * (_dist * panPerPixel);
            }
        }

        /// <summary>This frame's WASD movement in graph-local space, or zero. Scaled by the view distance so it feels the same at any zoom.</summary>
        private Vector3 FlyStep(Keyboard kb)
        {
            if (kb == null || ConsoleUI.IsTyping || kb.ctrlKey.isPressed) return Vector3.zero;
            float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            // Up and down are the graph's own vertical, not the camera's: Space rises and Shift sinks the same way
            // whichever way the view is pitched, as in a game's creative-mode flying.
            float y = (kb.spaceKey.isPressed ? 1f : 0f) - (kb.shiftKey.isPressed ? 1f : 0f);
            if (x == 0f && z == 0f && y == 0f) return Vector3.zero;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float speed = Mathf.Max(minDistance, _dist) * flySpeed;
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 dir = rot * new Vector3(x, 0f, z) + Vector3.up * y;
            return dir.sqrMagnitude > 1e-6f ? dir.normalized * (speed * dt) : Vector3.zero;
        }

        private Ray PointerRay(Mouse m)
        {
            return viewCamera.ScreenPointToRay(m.position.ReadValue());
        }

        /// <summary>Wheel input to a zoom. Windows reports +-120 per notch, other devices small fractions, so both are normalised.</summary>
        private void ZoomBy(float scroll)
        {
            float steps = Mathf.Abs(scroll) >= 10f ? scroll / 120f : scroll;
            steps = Mathf.Clamp(steps, -3f, 3f);
            float factor = Mathf.Exp(-steps * zoomStep); // wheel up = closer
            _zoomUntil = Time.unscaledTime + 0.6f;
            if (_locked)
            {
                float basis = Mathf.Max(_frameDist, 0.01f);
                _zoomMul = Mathf.Clamp(_zoomMul * factor, minDistance / basis, maxDistance / basis);
                _distT = Mathf.Clamp(_frameDist * _zoomMul, minDistance, maxDistance);
            }
            else
            {
                _distT = Mathf.Clamp(_distT * factor, minDistance, maxDistance);
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
