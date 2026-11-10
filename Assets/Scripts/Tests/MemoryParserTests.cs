using System.Collections.Generic;
using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    public class MemoryParserTests
    {
        // Backtick stands for a double quote, so the real payloads stay readable in a C# string.
        private static string Q(string s) => s.Replace('`', '"');

        private static string Escaped(string s) => Q(s).Replace("\"", "\\\"");

        // The real shapes captured from EVA's log: the python repr of an MCP result wrapping the vault tool's JSON.
        private const string ReadOut =
            "content=[{'type': 'text', 'text': '{`path`: `Eva/eva.md`, `title`: `eva`, `frontmatter`: {`type`: `moc`, " +
            "`fileClass`: `MocFile`, `owner`: `eva`, `status`: `active`, `created`: `2026-08-04`...";

        private const string SearchOut =
            "content=[{'type': 'text', 'text': '{`query`: `Eva AI, personality, self-description`, `count`: 10, `results`: " +
            "[{`path`: `Engineering/Roadmap.md`, ...}, {`path`: `Projects/Eva AI.md`, ...}]...";

        private static MemoryTouch Only(IReadOnlyList<MemoryTouch> t)
        {
            Assert.AreEqual(1, t.Count);
            return t[0];
        }

        [Test]
        public void VaultRead_RealOutputShape_IsReadOfThePathInTheOutput()
        {
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("fates_desktop__vault_read", null, Q(ReadOut), true);
            MemoryTouch m = Only(t);
            Assert.AreEqual("Eva/eva.md", m.Path);
            Assert.AreEqual(MemoryAccessKind.Read, m.Kind);
        }

        [Test]
        public void VaultSearch_RealOutputShape_EachResultIsAHit()
        {
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("fates_desktop__vault_search", "{`query`: `x`, `limit`: 10}".Replace('`', '"'), Q(SearchOut), true);
            Assert.AreEqual(2, t.Count);
            Assert.AreEqual("Engineering/Roadmap.md", t[0].Path);
            Assert.AreEqual("Projects/Eva AI.md", t[1].Path);
            Assert.AreEqual(MemoryAccessKind.Hit, t[0].Kind);
            Assert.AreEqual(MemoryAccessKind.Hit, t[1].Kind);
        }

        [Test]
        public void BackslashEscapedQuotes_AreAccepted()
        {
            IReadOnlyList<MemoryTouch> read = MemoryAccessParser.Parse("vault_read", null, Escaped(ReadOut), true);
            Assert.AreEqual("Eva/eva.md", Only(read).Path);

            IReadOnlyList<MemoryTouch> search = MemoryAccessParser.Parse("vault_search", null, Escaped(SearchOut), true);
            Assert.AreEqual(2, search.Count);
            Assert.AreEqual("Projects/Eva AI.md", search[1].Path);
        }

        [Test]
        public void TruncatedOutput_DropsTheValueWhoseClosingQuoteIsMissing()
        {
            string cut = Q("{`results`: [{`path`: `Projects/Eva AI.md`}, {`path`: `Engineering/Roa");
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("vault_search", null, cut, true);
            Assert.AreEqual("Projects/Eva AI.md", Only(t).Path);

            // Cut inside the only path: nothing usable, and no exception.
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, Q("{`path`: `Eva/ev"), true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, Q("{`path`: "), true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, Q("{`pat"), true).Count);
        }

        [Test]
        public void ACutValueInTheEscapedForm_IsAlsoDropped()
        {
            string cut = Escaped("{`path`: `Eva/ev");
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, cut, true).Count);
        }

        [Test]
        public void FailedCall_YieldsNothing()
        {
            Assert.AreEqual(0, MemoryAccessParser.Parse("memory__vault_read", Q("{`path`: `Eva/eva.md`}"), Q(ReadOut), false).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("memory__vault_search", null, Q(SearchOut), false).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("memory__vault_write_note", Q("{`path`: `a.md`}"), null, false).Count);
        }

        [TestCase("fates_desktop__read_file")]
        [TestCase("fates_desktop__run_command")]
        [TestCase("memory__kv_get")]
        [TestCase("memory__vault_list")]
        [TestCase("memory__vault_query")]
        [TestCase("")]
        [TestCase(null)]
        public void NonMemoryTool_YieldsNothingEvenIfItsOutputHasAPath(string tool)
        {
            Assert.IsFalse(MemoryAccessParser.IsMemoryTool(tool));
            Assert.AreEqual(0, MemoryAccessParser.Parse(tool, Q("{`path`: `Eva/eva.md`}"), Q(ReadOut), true).Count);
        }

        [TestCase("vault_search", MemoryAccessKind.Hit)]
        [TestCase("memory__vault_search", MemoryAccessKind.Hit)]
        [TestCase("fates_desktop__vault_read", MemoryAccessKind.Read)]
        [TestCase("memory__vault_write_note", MemoryAccessKind.Written)]
        [TestCase("memory__vault_append", MemoryAccessKind.Written)]
        public void ToolNames_MatchOnTheBareNameAfterTheServerPrefix(string tool, MemoryAccessKind expected)
        {
            Assert.IsTrue(MemoryAccessParser.TryKindOf(tool, out MemoryAccessKind kind));
            Assert.AreEqual(expected, kind);
        }

        [Test]
        public void SearchWithManyPaths_YieldsEveryOneOnceInOrder()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("{`count`: 12, `results`: [");
            for (int i = 0; i < 12; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("{`path`: `Folder").Append(i % 3).Append("/Note ").Append(i).Append(".md`, `score`: 0.5}");
            }
            sb.Append(", {`path`: `Folder0/Note 0.md`}]}");
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("vault_search", null, Q(sb.ToString()), true);
            Assert.AreEqual(12, t.Count, "the repeated path is one touch");
            Assert.AreEqual("Folder0/Note 0.md", t[0].Path);
            Assert.AreEqual("Folder2/Note 11.md", t[11].Path);
        }

        [Test]
        public void ReadWriteAppend_PreferTheInputPathOverTheOutputPath()
        {
            string input = Q("{`path`: `Projects/Eva AI.md`}");
            string output = Q(ReadOut);
            Assert.AreEqual("Projects/Eva AI.md", Only(MemoryAccessParser.Parse("vault_read", input, output, true)).Path);
            Assert.AreEqual("Projects/Eva AI.md", Only(MemoryAccessParser.Parse("vault_write_note", input, output, true)).Path);
            MemoryTouch app = Only(MemoryAccessParser.Parse("vault_append", input, output, true));
            Assert.AreEqual("Projects/Eva AI.md", app.Path);
            Assert.AreEqual(MemoryAccessKind.Written, app.Kind);
        }

        [Test]
        public void WhenTheInputHasNoPath_TheOutputPathIsTheFallback()
        {
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("vault_write_note", "{}", Q("{`path`: `Inbox/Idea.md`, `written`: true}"), true);
            Assert.AreEqual("Inbox/Idea.md", Only(t).Path);
        }

        [Test]
        public void SearchIgnoresThePathsInItsInput()
        {
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("vault_search", Q("{`query`: `x`, `path`: `Projects`}"), Q("{`results`: [{`path`: `Notes/A.md`}]}"), true);
            Assert.AreEqual("Notes/A.md", Only(t).Path);
        }

        [Test]
        public void KeysThatMerelyEndInPath_AreNotThePathKey()
        {
            string tricky = Q("{`abs_path`: `C:/x.md`, `filepath`: `y.md`, `note`: `the path is Eva/eva.md`}");
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, tricky, true).Count);
        }

        [Test]
        public void WhitespaceAroundTheColon_IsTolerated()
        {
            Assert.AreEqual("A/b.md", Only(MemoryAccessParser.Parse("vault_read", Q("{ `path` :   `A/b.md` }"), null, true)).Path);
            Assert.AreEqual("A/b.md", Only(MemoryAccessParser.Parse("vault_read", Q("{\n  `path`:\n  `A/b.md`\n}"), null, true)).Path);
        }

        [Test]
        public void PathsAreNormalised()
        {
            Assert.AreEqual("A/b.md", Only(MemoryAccessParser.Parse("vault_read", Q("{`path`: `./A//b.md`}"), null, true)).Path);
            Assert.AreEqual("A/b.md", Only(MemoryAccessParser.Parse("vault_read", Q("{`path`: `/A/b.md`}"), null, true)).Path);
            Assert.AreEqual("A/b.md", Only(MemoryAccessParser.Parse("vault_read", Q("{`path`: `A\\\\b.md`}"), null, true)).Path);
        }

        [Test]
        public void JsonUnicodeAndSlashEscapes_AreDecoded()
        {
            Assert.AreEqual("Eva\u2019s/Notes.md", Only(MemoryAccessParser.Parse("vault_read", Q("{`path`: `Eva\\u2019s\\/Notes.md`}"), null, true)).Path);
            // The repr doubles the backslash of a \uXXXX escape.
            Assert.AreEqual("Eva\u2019s.md", Only(MemoryAccessParser.Parse("vault_read", null, Escaped("{`path`: `Eva\\\\u2019s.md`}"), true)).Path);
        }

        [Test]
        public void ImplausiblePaths_AreRejected()
        {
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", Q("{`path`: ``}"), null, true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", Q("{`path`: `   `}"), null, true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", Q("{`path`: `../secrets.md`}"), null, true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", Q("{`path`: `a\\nb.md`}"), null, true).Count, "a JSON newline escape is not a path char");
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", "{\"path\": \"a\u0001b.md\"}", null, true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", Q("{`path`: `" + new string('x', MemoryAccessParser.MaxPathLength + 1) + ".md`}"), null, true).Count);
            Assert.AreEqual(1, MemoryAccessParser.Parse("vault_read", Q("{`path`: `" + new string('x', MemoryAccessParser.MaxPathLength - 3) + ".md`}"), null, true).Count);
        }

        [Test]
        public void GarbageAndNulls_NeverThrow()
        {
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_read", null, null, true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_search", "", "", true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_search", null, "\\\\\\\"\"\"path\"\"\"", true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_search", null, "\"path\"", true).Count);
            Assert.AreEqual(0, MemoryAccessParser.Parse("vault_search", null, "\"path\": \"", true).Count);
        }

        [Test]
        public void AValueWithAnEscapedClosingQuoteInsideTheEscapedForm_EndsAtTheQuote()
        {
            // \"path\": \"A/b.md\", \"x\": 1 : the closing quote is the \" right after .md, and the backslash is not part of the value.
            IReadOnlyList<MemoryTouch> t = MemoryAccessParser.Parse("vault_read", null, Escaped("{`path`: `A/b.md`, `x`: 1}"), true);
            Assert.AreEqual("A/b.md", Only(t).Path);
        }
    }
}
