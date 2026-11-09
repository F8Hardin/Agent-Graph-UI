using NUnit.Framework;
using UnityEngine;
using AgentConsole.View;

namespace AgentConsole.Tests
{
    /// <summary>
    /// The white-theme faults, pinned: a backdrop that is not blown out, and edges that read on it. Both are
    /// properties of the token formula, so they are checked for every preset rather than for one example.
    /// </summary>
    public class ThemeTests
    {
        private static ThemeTokens Tokens(ThemePreset p) => ThemeTokens.Compute(ThemePresets.BaseOf(p));

        [Test]
        public void Stage_IsTheBackgroundOnDarkThemes_AndASoftGreyOnLightOnes()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                ThemeTokens t = Tokens(p);
                float bg = ThemeTokens.Lum(t.Bg);
                float stage = ThemeTokens.Lum(t.Stage);
                if (p.Dark)
                {
                    Assert.AreEqual(bg, stage, 0.005f, p.Key + ": a dark stage must stay as it was");
                }
                else
                {
                    Assert.Less(stage, bg - 0.06f, p.Key + ": the stage must fall below the white page");
                    Assert.Greater(stage, 0.7f, p.Key + ": soft grey, not a dark room");
                }
            }
        }

        [Test]
        public void Edges_ReachTheirContrastTargetAgainstTheStage_OnEveryTheme()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                ThemeTokens t = Tokens(p);
                float ys = ThemeTokens.RelativeLuminance(t.Stage);
                float c1 = ThemeTokens.Contrast(ys, ThemeTokens.RelativeLuminance(t.Link));
                float c2 = ThemeTokens.Contrast(ys, ThemeTokens.RelativeLuminance(t.Link2));
                Assert.GreaterOrEqual(c1, 2.1f, p.Key + ": agent-server edge");
                Assert.GreaterOrEqual(c2, 1.5f, p.Key + ": server-tool edge");
                Assert.Greater(c1, c2, p.Key + ": the nearer tier is the stronger line");
            }
        }

        [Test]
        public void Edges_AreNotHeavyOnDark_AndDoNotLetTheOldFixedMixBackIn()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                if (!p.Dark) continue;
                ThemeTokens t = Tokens(p);
                float ys = ThemeTokens.RelativeLuminance(t.Stage);
                float c1 = ThemeTokens.Contrast(ys, ThemeTokens.RelativeLuminance(t.Link));
                Assert.Less(c1, 3.0f, p.Key + ": a thin faint line on black, not a bright wire");
            }
        }

        [Test]
        public void OneFormula_GivesLightThemesTheSameContrastAsDarkOnes()
        {
            ThemeTokens dark = Tokens(ThemePresets.Find("green-k"));
            ThemeTokens light = Tokens(ThemePresets.Find("green-w"));
            float cd = ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(dark.Stage), ThemeTokens.RelativeLuminance(dark.Link));
            float cl = ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(light.Stage), ThemeTokens.RelativeLuminance(light.Link));
            Assert.AreEqual(cd, cl, 0.15f, "same contrast, reached by a different mix");
            Assert.Greater(ThemeTokens.Lum(light.Link), 0f);
        }

        [Test]
        public void MixForContrast_StopsAtTheSmallestMixThatReachesTheRatio()
        {
            Color from = new Color(0.8f, 0.8f, 0.8f, 1f);
            Color got = ThemeTokens.MixForContrast(from, Color.black, 3f);
            float y = ThemeTokens.RelativeLuminance(from);
            Assert.GreaterOrEqual(ThemeTokens.Contrast(y, ThemeTokens.RelativeLuminance(got)), 3f - 0.02f);
            Assert.Less(ThemeTokens.Contrast(y, ThemeTokens.RelativeLuminance(got)), 3.3f, "no heavier than asked");
        }

        [Test]
        public void MixForContrast_LeavesAColourAloneThatAlreadyReads()
        {
            Color ink = new Color(0.04f, 0.2f, 0.1f, 1f);
            Color backdrop = new Color(0.9f, 0.9f, 0.9f, 1f);
            Color got = ThemeTokens.MixForContrast(ink, Color.black, backdrop, 4.5f);
            Assert.AreEqual(ink.r, got.r, 1e-5f);
            Assert.AreEqual(ink.g, got.g, 1e-5f);
        }

        [Test]
        public void MixForContrast_GivesUpAtItsCeilingRatherThanLoopingOrOvershooting()
        {
            Color got = ThemeTokens.MixForContrast(Color.white, Color.white, 21f);
            Assert.AreEqual(1f, got.r, 1e-5f);
        }

        [Test]
        public void BubbleTokens_AreTheSpeakersTintMixedTowardTheBackground()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                ThemeTokens t = Tokens(p);
                Assert.AreEqual(ThemeTokens.Mix(t.Bg, t.Accent, 0.18f).g, t.BubbleUser.g, 1e-5f, p.Key + ": user bubble");
                Assert.AreEqual(ThemeTokens.Mix(t.Bg, t.Accent, 0.12f).g, t.BubbleEva.g, 1e-5f, p.Key + ": agent bubble");
                Assert.AreEqual(ThemeTokens.Mix(t.Bg, t.Bad, 0.18f).r, t.BubbleBad.r, 1e-5f, p.Key + ": error bubble");
            }
        }

        [Test]
        public void BubbleTokens_CanBeOverriddenWithoutTouchingTheBaseFive()
        {
            ThemeBase b = ThemePresets.BaseOf(ThemePresets.Find("green-k"));
            b.BubbleUser = new Color(0.2f, 0.1f, 0.4f, 1f);
            b.BubbleEva = new Color(0.1f, 0.3f, 0.2f, 1f);
            ThemeTokens t = ThemeTokens.Compute(b);
            Assert.AreEqual(0.2f, t.BubbleUser.r, 1e-5f);
            Assert.AreEqual(0.3f, t.BubbleEva.g, 1e-5f);
            Assert.AreEqual(b.Bg.r, t.Bg.r, 1e-5f);
        }

        [Test]
        public void BubbleInk_ReadsOnEveryBubbleFill_OnEveryTheme()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                ThemeTokens t = Tokens(p);
                float yi = ThemeTokens.RelativeLuminance(t.BubbleInk);
                Assert.GreaterOrEqual(ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.BubbleUser), yi), 4.45f, p.Key + ": on the user bubble");
                Assert.GreaterOrEqual(ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.BubbleEva), yi), 4.45f, p.Key + ": on the agent bubble");
                Assert.GreaterOrEqual(ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.BubbleBad), yi), 4.45f, p.Key + ": on the error bubble");
                Assert.GreaterOrEqual(ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.Code), yi), 4.45f, p.Key + ": on a code block");
            }
        }

        [Test]
        public void CodeBlocks_AreDarkerThanTheAgentBubble_OnEveryTheme()
        {
            foreach (ThemePreset p in ThemePresets.All)
            {
                ThemeTokens t = Tokens(p);
                Assert.Less(ThemeTokens.Lum(t.Code), ThemeTokens.Lum(t.BubbleEva), p.Key);
            }
        }
    }
}
