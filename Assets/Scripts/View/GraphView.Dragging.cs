using System;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>Grabbing, dragging and clicking nodes. Split out of GraphView.cs, which was already past the size limit.</summary>
    public sealed partial class GraphView
    {
        [Header("Dragging")]
        [Tooltip("Layout alpha held while a node is being dragged. Higher = the neighbours react harder. After release it cools to rest and freezes.")]
        [SerializeField, Range(0.05f, 1f)] private float dragHeat = 0.35f;
        [Tooltip("The node must move this far (world units) before a press becomes a drag, so a plain click disturbs nothing.")]
        [SerializeField, Min(0f)] private float dragStartDistance = 0.15f;
        [Tooltip("A dragged node is kept within this many ToolShell radii of the agent.")]
        [SerializeField, Min(1f)] private float dragMaxShells = 2.5f;

        private int _dragIndex = -1;
        private bool _dragMoved;
        private bool _dragDirty;
        private Vector3 _dragOffset;
        private Vector3 _dragStartWorld;
        private Vector3 _dragTargetWorld;
        [Tooltip("Two clicks on the same node within this many seconds are a double-click.")]
        [SerializeField, Min(0.1f)] private float doubleClickSeconds = 0.35f;
        private int _lastClickIndex = -1;
        private float _lastClickAt = -10f;

        /// <summary>A node was double-clicked: (its centre in graph-local space, the radius that frames it and its children).</summary>
        public event Action<Vector3, float> NodeFocusRequested;

        /// <summary>A press and release on a node that never crossed the drag threshold. Argument is the index in GraphModel.Nodes order.</summary>
        public event Action<int> NodeClicked;

        public bool IsDragging => _dragIndex >= 0;
        public int DraggedIndex => _dragIndex;

        public bool TryGetNodeIndex(string id, out int index) => _nodeIndex.TryGetValue(id ?? string.Empty, out index);

        /// <summary>World position of a node's centre. Index is the order of GraphModel.Nodes.</summary>
        public Vector3 NodeWorldPosition(int index) => _views[index].Root.position;

        /// <summary>Press: raycast the sphere colliders. True if a node was hit and is now grabbed.</summary>
        public bool BeginDrag(Ray ray)
        {
            if (_dragIndex >= 0 || _views.Count == 0) return false;
            Physics.SyncTransforms(); // the colliders follow the layout, which moves transforms every frame
            if (!Physics.Raycast(ray, out RaycastHit hit, 1000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            for (int i = 0; i < _views.Count; i++)
            {
                if (_views[i].Collider == hit.collider) return BeginDragNode(i, hit.point);
            }
            return false;
        }

        /// <summary>
        /// Grab a node by index, the agent included. The agent is the layout's anchor: grabbing it moves the whole
        /// graph, because the shells and the clamp are measured from it (see ForceLayout3D.AnchorIndex). It used to
        /// be refused, so a press on it fell through to the camera orbit; now the press belongs to the node, which
        /// also means a plain click on it can never start an orbit.
        /// </summary>
        public bool BeginDragNode(int index, Vector3 grabPointWorld)
        {
            if (_dragIndex >= 0 || index < 0 || index >= _views.Count) return false;
            Vector3 nodeWorld = _views[index].Root.position;
            _dragIndex = index;
            _dragOffset = nodeWorld - grabPointWorld; // keep the grab point under the cursor instead of jumping the centre to it
            _dragStartWorld = nodeWorld;
            _dragTargetWorld = nodeWorld;
            _dragMoved = false;
            _layout.SetPinned(index, true);
            _views[index].SetSelected(true);
            return true;
        }

        /// <summary>Move: drag in the plane through the node that faces the camera.</summary>
        public void DragTo(Ray ray)
        {
            if (_dragIndex < 0) return;
            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;
            Plane plane = new Plane(-cam.transform.forward, _views[_dragIndex].Root.position);
            if (!plane.Raycast(ray, out float enter)) return;
            DragToWorld(ray.GetPoint(enter) + _dragOffset);
        }

        /// <summary>Where the grabbed node is being asked to be, in world space. Starts at the node and moves by <see cref="DragBy"/>.</summary>
        public Vector3 DragTargetWorld => _dragTargetWorld;

        /// <summary>
        /// Move the grabbed node by a world-space offset. This is what the camera rig uses: it turns the pointer's own
        /// movement (and WASD) into an offset, so the node copies the pointer's motion. Rejected: placing the node where
        /// the pointer ray meets a plane (DragTo), because the camera can move while a node is held, and then a pointer
        /// that is not moving still sweeps the ray, the node chases it, the camera chases the node, and the grabbed
        /// agent flew off into space.
        /// </summary>
        public void DragBy(Vector3 worldDelta)
        {
            if (_dragIndex < 0) return;
            _dragTargetWorld += worldDelta;
            DragToWorld(_dragTargetWorld);
        }

        /// <summary>Pin the grabbed node at a world position and re-heat the layout so the rest reacts.</summary>
        public void DragToWorld(Vector3 worldPoint)
        {
            if (_dragIndex < 0) return;
            if (!_dragMoved)
            {
                if ((worldPoint - _dragStartWorld).sqrMagnitude < dragStartDistance * dragStartDistance) return;
                _dragMoved = true;
            }
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            if (_dragIndex != _layout.AnchorIndex)
            {
                // Relative to the agent, not the origin: the agent may have been moved. The agent itself is not
                // clamped; there is nothing to stay near, and the pointer cannot leave the screen anyway.
                Vector3 c = _layout.AnchorPosition;
                local = c + Vector3.ClampMagnitude(local - c, layout.ToolShell * dragMaxShells);
            }
            _layout.SetPosition(_dragIndex, local);
            _layout.Heat(dragHeat); // every frame while held: the springs pull the neighbours after it
            _dragDirty = true;
        }

        /// <summary>Release: let the layout cool to rest. A release that never crossed the drag threshold is a click.</summary>
        public void EndDrag() => ReleaseDrag(true);

        private void ReleaseDrag(bool raiseClick)
        {
            if (_dragIndex < 0) return;
            int index = _dragIndex;
            bool moved = _dragMoved;
            // Unpinning the agent is a no-op in the layout: a dropped agent stays exactly where it was dropped.
            // A home spring that returns it to the origin was proposed and rejected by the user.
            _layout.SetPinned(index, false);
            _views[index].SetSelected(false);
            if (moved) _layout.Heat(dragHeat);
            _dragIndex = -1;
            _dragMoved = false;
            if (!moved && raiseClick) Click(index);
        }

        /// <summary>The visible response to a click for now: a short glow pulse, and the camera focuses the node like a tool call does.</summary>
        private void Click(int index)
        {
            Run(Op.Pulse, index, 1f, false);
            NodeClicked?.Invoke(index);
            // A single click only pulses. Moving the camera is the double-click's job: a view that jumps on every
            // click makes it impossible to pick a node up without losing your place.
            float now = Time.unscaledTime;
            if (index == _lastClickIndex && now - _lastClickAt <= doubleClickSeconds)
            {
                _lastClickIndex = -1;
                FocusNode(index);
            }
            else
            {
                _lastClickIndex = index;
                _lastClickAt = now;
            }
        }

        /// <summary>Ask the camera to frame a node together with the nodes hanging directly off it.</summary>
        public void FocusNode(int index)
        {
            if (index < 0 || index >= _views.Count) return;
            Vector3 centre = _views[index].Root.localPosition;
            float radius = _views[index].Radius;
            for (int e = 0; e < _linkSrc.Count; e++)
            {
                if (_linkSrc[e] != index) continue;
                NodeView child = _views[_linkDst[e]];
                radius = Mathf.Max(radius, (child.Root.localPosition - centre).magnitude + child.Radius);
            }
            NodeFocusRequested?.Invoke(centre, radius);
        }
    }
}
