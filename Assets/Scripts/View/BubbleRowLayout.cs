using UnityEngine;
using UnityEngine.UI;

namespace AgentConsole.View
{
    /// <summary>
    /// Lays one bubble in a row: as wide as its text wants, capped at a fraction of the row, pushed to one side.
    /// A plain layout group cannot do this: the cap is relative to the row, and a short reply must hug its text
    /// instead of stretching to the cap. The bubble's width is fixed in the horizontal pass, before any text is
    /// measured vertically, so its wrapped height is the height at the width it really got.
    /// </summary>
    internal sealed class BubbleRowLayout : LayoutGroup
    {
        public bool AlignRight;
        public float MaxFraction = 0.75f;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            float pref = 0f;
            for (int i = 0; i < rectChildren.Count; i++) pref = Mathf.Max(pref, LayoutUtility.GetPreferredWidth(rectChildren[i]));
            SetLayoutInputForAxis(0f, -1f, pref + padding.horizontal, 1f, 0);
        }

        public override void SetLayoutHorizontal()
        {
            float avail = rectTransform.rect.width - padding.horizontal;
            float cap = Mathf.Max(0f, avail * MaxFraction);
            for (int i = 0; i < rectChildren.Count; i++)
            {
                RectTransform c = rectChildren[i];
                float w = Mathf.Min(LayoutUtility.GetPreferredWidth(c), cap);
                float x = AlignRight ? padding.left + avail - w : padding.left;
                SetChildAlongAxis(c, 0, x, w);
            }
        }

        public override void CalculateLayoutInputVertical()
        {
            float h = 0f;
            for (int i = 0; i < rectChildren.Count; i++) h = Mathf.Max(h, LayoutUtility.GetPreferredHeight(rectChildren[i]));
            SetLayoutInputForAxis(h + padding.vertical, h + padding.vertical, h + padding.vertical, 0f, 1);
        }

        public override void SetLayoutVertical()
        {
            for (int i = 0; i < rectChildren.Count; i++)
            {
                RectTransform c = rectChildren[i];
                SetChildAlongAxis(c, 1, padding.top, LayoutUtility.GetPreferredHeight(c));
            }
        }
    }
}
