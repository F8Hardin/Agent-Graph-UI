using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The body of a node: the mesh, its material and everything that restyles it. <see cref="NodeView"/> keeps the
    /// label, the picking collider and the damped billboard, and swaps this object when the style changes, so a
    /// style switch never rebuilds the label or disturbs the layout.
    ///
    /// To add a style: subclass this, then register it in <see cref="NodeStyleRegistry"/>. The base class owns the
    /// body GameObject and a per-node material instance cloned from the style's template, so the subclass only has
    /// to push values into <see cref="Mat"/>.
    /// </summary>
    internal abstract class NodeVisual
    {
        protected readonly GraphNode Node;
        protected readonly float Radius;
        protected readonly Material Mat;
        private readonly GameObject _body;

        protected NodeVisual(GraphNode node, float radius, Transform root, Mesh sphere, Material template)
        {
            Node = node;
            Radius = radius;
            _body = new GameObject("Body");
            _body.transform.SetParent(root, false);
            _body.transform.localScale = Vector3.one * (radius * 2f);
            _body.AddComponent<MeshFilter>().sharedMesh = sphere;
            MeshRenderer mr = _body.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Mat = new Material(template) { name = template.name + " " + node.Label };
            mr.sharedMaterial = Mat;
        }

        /// <summary>Push the theme into the material. Runs on a theme change only, so it may allocate nothing but need not be tuned.</summary>
        public abstract void ApplyTheme(ThemeTokens t);

        /// <summary>Every frame. Allocation-free; time-based smoothing uses 1 - exp(-k*dt).</summary>
        public abstract void Render(float glow, float dt);

        /// <summary>True while the node is being dragged: the body shows a brighter rim.</summary>
        public abstract void SetSelected(bool selected);

        /// <summary>
        /// The effective colour behind the label, resting or fully lit. NodeView picks readable ink against it, so a
        /// style must report what its centre really looks like, not what its rim looks like.
        /// </summary>
        public abstract Color BackdropFor(ThemeTokens t, bool lit);

        public virtual void Destroy()
        {
            if (Mat != null) Object.Destroy(Mat);
            if (_body != null) Object.Destroy(_body);
        }
    }
}
