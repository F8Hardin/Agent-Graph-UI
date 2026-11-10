using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>
    /// The theme's colours for an edge material, in one place. The main graph and the session memory tree each own an
    /// EdgeMesh instance and both call this, so a tree edge always matches a graph edge. (It was inline in GraphView, which
    /// is already past the size limit; moving it out also shortened that file.)
    /// </summary>
    internal static class EdgeTheme
    {
        public static void Apply(Material m, ThemeTokens t)
        {
            // The edges take the node colours of the tier they lead to, so tube and dome read as one material.
            // (Was Link/Link2, solved for minimum contrast: correct but so faint the edges vanished at a distance.)
            Color l1 = t.NSrv;
            l1.a = 0.9f;
            Color l2 = Color.Lerp(t.NTool, t.NSrv, 0.5f);
            l2.a = 0.9f;
            Color down = t.Bad;
            down.a = 0.7f;
            m.SetColor("_LinkColor1", l1);
            m.SetColor("_LinkColor2", l2);
            m.SetColor("_FlowColor", t.Flow);
            m.SetColor("_MemColor", t.Mem);
            m.SetColor("_DownColor", down);
        }
    }
}
