using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    /// <summary>
    /// The Markdown to TMP converter, headless. Model output is untrusted text going into a markup language, so the
    /// escape tests matter as much as the formatting ones.
    /// </summary>
    public class MarkdownTests
    {
        private static readonly MarkdownStyle Style = new MarkdownStyle
        {
            Accent = "#AAAAAA", Heading = "#BBBBBB", Muted = "#CCCCCC", CodeMark = "#DDDDDD40"
        };

        private static List<MdBlock> Blocks(string md)
        {
            List<MdBlock> list = new List<MdBlock>();
            MarkdownTmp.Parse(md, Style, list);
            return list;
        }

        /// <summary>The text blocks joined, which is the whole output when there is no fenced code.</summary>
        private static string Text(string md)
        {
            StringBuilder sb = new StringBuilder();
            foreach (MdBlock b in Blocks(md))
            {
                if (b.Kind == MdBlockKind.Text) sb.Append(b.Text);
            }
            return sb.ToString();
        }

        [Test]
        public void Bold_BecomesBoldTags()
        {
            Assert.AreEqual("a <b>b</b> c", Text("a **b** c"));
            Assert.AreEqual("a <b>b</b> c", Text("a __b__ c"));
        }

        [Test]
        public void Italic_BecomesItalicTags()
        {
            Assert.AreEqual("a <i>b</i> c", Text("a *b* c"));
            Assert.AreEqual("a <i>b</i> c", Text("a _b_ c"));
        }

        [Test]
        public void BoldItalicAndNesting_Compose()
        {
            Assert.AreEqual("<b><i>x</i></b>", Text("***x***"));
            Assert.AreEqual("<b>a <i>b</i> c</b>", Text("**a *b* c**"));
            Assert.AreEqual("<i>a <b>b</b> c</i>", Text("*a **b** c*"));
        }

        [Test]
        public void Strikethrough_BecomesStrikeTags()
        {
            Assert.AreEqual("<s>gone</s>", Text("~~gone~~"));
        }

        [Test]
        public void SnakeCaseNames_AreNotEmphasis()
        {
            string t = Text("run vault_read_note then my_tool_name");
            Assert.AreEqual("run vault_read_note then my_tool_name", t);
        }

        [Test]
        public void UnmatchedOrSpacedMarkers_StayLiteral()
        {
            Assert.AreEqual("2 * 3 * 4", Text("2 * 3 * 4"));
            Assert.AreEqual("**oops", Text("**oops"));
            Assert.AreEqual("5 stars*", Text("5 stars*"));
        }

        [Test]
        public void BackslashEscapes_KeepTheMarkerLiteral()
        {
            Assert.AreEqual("*not italic*", Text("\\*not italic\\*"));
        }

        [Test]
        public void InlineCode_IsMarkedAndNothingInsideIsParsed()
        {
            string t = Text("use `a*b*c` here");
            Assert.AreEqual("use <mark=#DDDDDD40> a*b*c </mark> here", t);
            Assert.IsFalse(t.Contains("<i>"));
        }

        [Test]
        public void InlineCode_HidesAMarkerFromEmphasisAround()
        {
            Assert.AreEqual("<b>x <mark=#DDDDDD40> ** </mark> y</b>", Text("**x `**` y**"));
        }

        [Test]
        public void UnclosedBacktick_StaysLiteral()
        {
            Assert.AreEqual("a `b", Text("a `b"));
        }

        [Test]
        public void AngleBrackets_AreEscapedSoTmpCannotObeyThem()
        {
            string t = Text("<b>hi</b> and <size=900>big");
            Assert.IsFalse(t.Contains("<b>"), "a model's <b> must not become a bold tag");
            Assert.IsFalse(t.Contains("<size=900>"));
            Assert.IsTrue(t.Contains("<noparse><</noparse>b>hi<noparse><</noparse>/b>"));
        }

        [Test]
        public void Escape_WrapsEachLessThanOnItsOwn_SoNoContentCanCloseItEarly()
        {
            string t = MarkdownTmp.Escape("</noparse><color=red>");
            Assert.AreEqual("<noparse><</noparse>/noparse><noparse><</noparse>color=red>", t);
        }

        [Test]
        public void Escape_LeavesPlainTextAlone()
        {
            Assert.AreEqual("plain > text", MarkdownTmp.Escape("plain > text"));
            Assert.AreEqual(string.Empty, MarkdownTmp.Escape(null));
        }

        [Test]
        public void EscapingAppliesInsideCodeSpansAndLinks()
        {
            Assert.IsFalse(Text("`<b>`").Contains("<b>"));
            Assert.IsFalse(Text("[<i>x</i>](http://a)").Contains("<i>x"));
        }

        [Test]
        public void BulletList_UsesBulletsWithAHangingIndent()
        {
            string t = Text("- one\n- two");
            Assert.AreEqual(2, Count(t, "•"));
            Assert.AreEqual(2, Count(t, "<indent=1.2em><line-indent=-1.2em>"));
            Assert.IsTrue(t.Contains("</color> one</indent><line-indent=0>\n"));
            Assert.IsTrue(t.EndsWith("</color> two</indent><line-indent=0>"));
        }

        [Test]
        public void StarAndPlusBullets_AreLists_ButBoldAtLineStartIsNot()
        {
            Assert.AreEqual(1, Count(Text("* item"), "•"));
            Assert.AreEqual(1, Count(Text("+ item"), "•"));
            Assert.AreEqual("<b>bold</b> start", Text("**bold** start"));
        }

        [Test]
        public void NumberedList_KeepsItsNumbers_AndWidensTheHangForTwoDigits()
        {
            string t = Text("1. a\n2. b");
            Assert.IsTrue(t.Contains("1.</color> a"));
            Assert.IsTrue(t.Contains("2.</color> b"));
            Assert.IsTrue(Text("10. ten").Contains("<indent=2.4em><line-indent=-2.4em>"), "10. plus a space is four cells");
        }

        [Test]
        public void NestedList_IsIndentedByDepth()
        {
            string t = Text("- a\n  - b");
            Assert.IsTrue(t.Contains("<indent=1.2em>"));
            Assert.IsTrue(t.Contains("<indent=2.4em>"));
        }

        [Test]
        public void ListItems_ParseTheirInlineMarkup()
        {
            Assert.IsTrue(Text("- a **b**").Contains("<b>b</b>"));
        }

        [Test]
        public void FencedCode_IsItsOwnBlock_KeepingWhitespaceAndLanguage()
        {
            List<MdBlock> b = Blocks("before\n```csharp\nvar x = 1;\n    indented\n```\nafter");
            Assert.AreEqual(3, b.Count);
            Assert.AreEqual(MdBlockKind.Text, b[0].Kind);
            Assert.AreEqual("before", b[0].Text);
            Assert.AreEqual(MdBlockKind.Code, b[1].Kind);
            Assert.AreEqual("csharp", b[1].Lang);
            Assert.AreEqual("var x = 1;\n    indented", b[1].Text);
            Assert.AreEqual(MdBlockKind.Text, b[2].Kind);
            Assert.AreEqual("after", b[2].Text);
        }

        [Test]
        public void FencedCode_IsNotInterpreted_ButItsAngleBracketsAreEscaped()
        {
            List<MdBlock> b = Blocks("```\n**not bold** <b>x</b>\n```");
            Assert.AreEqual(1, b.Count);
            Assert.IsFalse(b[0].Text.Contains("<b>"));
            Assert.IsTrue(b[0].Text.StartsWith("**not bold**"));
        }

        [Test]
        public void UnclosedFence_IsStillACodeBlock_BecauseItIsMidStream()
        {
            List<MdBlock> b = Blocks("intro\n```py\nprint(1)");
            Assert.AreEqual(2, b.Count);
            Assert.AreEqual(MdBlockKind.Code, b[1].Kind);
            Assert.AreEqual("print(1)", b[1].Text);
        }

        [Test]
        public void TwoFences_GiveTwoCodeBlocks()
        {
            List<MdBlock> b = Blocks("```\na\n```\n\n```\nb\n```");
            Assert.AreEqual(2, b.Count);
            Assert.AreEqual("a", b[0].Text);
            Assert.AreEqual("b", b[1].Text);
        }

        [Test]
        public void Headings_ScaleByLevelAndTakeTheHeadingColour()
        {
            Assert.AreEqual("<size=140%><b><color=#BBBBBB>Title</color></b></size>", Text("# Title"));
            Assert.IsTrue(Text("## Two").StartsWith("<size=125%>"));
            Assert.IsTrue(Text("### Three").StartsWith("<size=112%>"));
            Assert.AreEqual("#hashtag", Text("#hashtag"), "no space after the hash: not a heading");
        }

        [Test]
        public void ListAndQuote_ResetTheFirstLineIndent_SoTheNextParagraphIsNotPulledLeft()
        {
            Assert.IsTrue(Text("- a\n\nafter").Contains("</indent><line-indent=0>\n" + MarkdownTmp.GapTag + "after"));
            Assert.IsTrue(Text("> q").EndsWith("</indent><line-indent=0>"));
        }

        [Test]
        public void Heading_DropsClosingHashes_AndParsesInline()
        {
            string t = Text("## A `b` ##");
            Assert.IsTrue(t.Contains("<mark="));
            Assert.IsFalse(t.Contains("##"));
        }

        [Test]
        public void Link_IsUnderlinedInTheAccent()
        {
            Assert.AreEqual("<link=\"http://a.b/c\"><u><color=#AAAAAA>site</color></u></link>", Text("[site](http://a.b/c)"));
        }

        [Test]
        public void Link_UrlCannotBreakOutOfItsAttribute()
        {
            string t = Text("[x](http://a\"><color=red>)");
            Assert.IsFalse(t.Contains("<color=red>"));
        }

        [Test]
        public void BracketsWithoutAUrl_AreText()
        {
            Assert.AreEqual("see [1] and [two]", Text("see [1] and [two]"));
        }

        [Test]
        public void Blockquote_IsMutedItalicWithABar()
        {
            string t = Text("> quoted *text*");
            Assert.IsTrue(t.Contains("<b>|</b>"));
            Assert.IsTrue(t.Contains("<color=#CCCCCC><i>"));
            Assert.IsTrue(t.Contains("quoted <i>text</i>"));
        }

        [Test]
        public void BlankLine_BecomesAShortGap_AndRepeatsCollapse()
        {
            Assert.AreEqual("a\n" + MarkdownTmp.GapTag + "b", Text("a\n\n\n\nb"));
            Assert.AreEqual("a\nb", Text("a\nb"));
        }

        [Test]
        public void LeadingAndTrailingBlankLines_AddNothing()
        {
            Assert.AreEqual("a", Text("\n\na\n\n"));
        }

        [Test]
        public void Heading_AfterAParagraph_GetsAGapEvenWithoutABlankLine()
        {
            Assert.IsTrue(Text("para\n# Head").Contains("\n" + MarkdownTmp.GapTag + "<size=140%>"));
        }

        [Test]
        public void Rule_IsRenderedMuted()
        {
            Assert.IsTrue(Text("---").StartsWith("<color=#CCCCCC>"));
            Assert.IsTrue(Text("* * *").StartsWith("<color=#CCCCCC>"));
        }

        [Test]
        public void WindowsLineEndings_AreNormalised()
        {
            Assert.AreEqual("a\nb", Text("a\r\nb"));
        }

        [Test]
        public void EmptyAndNull_GiveNoBlocks()
        {
            Assert.AreEqual(0, Blocks(null).Count);
            Assert.AreEqual(0, Blocks(string.Empty).Count);
            Assert.AreEqual(0, Blocks("\n\n").Count);
        }

        [Test]
        public void ParseReusesTheList_ItIsClearedEachTime()
        {
            List<MdBlock> list = new List<MdBlock>();
            MarkdownTmp.Parse("a\n```\nb\n```", Style, list);
            Assert.AreEqual(2, list.Count);
            MarkdownTmp.Parse("c", Style, list);
            Assert.AreEqual(1, list.Count);
        }

        [Test]
        public void AReplyThatGrowsTokenByToken_NeverThrows_AndEndsAsTextCodeText()
        {
            const string full = "# Plan\n\nUse **bold**, *it*, `code` and [a link](http://x.y).\n\n- one\n- two\n\n```cs\nint a = 1 < 2;\n```\n\n> quote\n";
            for (int n = 1; n <= full.Length; n++)
            {
                List<MdBlock> b = Blocks(full.Substring(0, n));
                Assert.AreEqual(MdBlockKind.Text, b[0].Kind, "prefix of " + n + " chars");
            }
            Assert.AreEqual(3, Blocks(full).Count, "text, code, text");
        }

        private static int Count(string s, string needle)
        {
            int n = 0;
            int i = 0;
            while ((i = s.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }
    }
}
