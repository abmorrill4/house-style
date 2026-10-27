using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Every House Style write raises Writing before it and Wrote after it, on the writing thread.</summary>
    public class WriteTrackedTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-write-" + Guid.NewGuid().ToString("N"));

        public WriteTrackedTests() { Directory.CreateDirectory(_root); }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private const string Ns = "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head /><body>";

        private string Topic(string body, string name = "t.htm")
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, Ns + body + "</body></html>");
            return path;
        }

        private static ActionRunner Runner()
        {
            return new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, new FakeStyleService());
        }

        [Fact]
        public void ApplyFixes_RaisesWritingThenWrote_WithTheDocumentPath()
        {
            var path = Topic("<p>Please utilize the tool.</p>");
            var doc = new FakeDocumentAdapter(path, true);
            var runner = Runner();
            var f = runner.Lint(runner.PickAllBlocks(doc)).Single(x => x.Excerpt == "utilize");
            var me = Thread.CurrentThread.ManagedThreadId;
            var log = new List<string>();
            var unchangedWhenWriting = false;
            Action<string> writing = p =>
            {
                if (Thread.CurrentThread.ManagedThreadId != me) return;
                log.Add("writing:" + p);
                unchangedWhenWriting = doc.Document.OuterXml.Contains("utilize");
            };
            Action<string> wrote = p => { if (Thread.CurrentThread.ManagedThreadId == me) log.Add("wrote:" + p); };
            ActionRunner.Writing += writing;
            ActionRunner.Wrote += wrote;
            try
            {
                Assert.Equal(1, runner.ApplyFixes(doc, new[] { f }, "fix"));
            }
            finally
            {
                ActionRunner.Writing -= writing;
                ActionRunner.Wrote -= wrote;
            }
            Assert.Equal(new[] { "writing:" + path, "wrote:" + path }, log.ToArray());
            Assert.True(unchangedWhenWriting);
        }

        [Fact]
        public void Writing_ThatMarksTheIssueFixed_SkipsItsWrite()
        {
            var path = Topic("<p>Please utilize the tool.</p>");
            var doc = new FakeDocumentAdapter(path, true);
            var runner = Runner();
            var f = runner.Lint(runner.PickAllBlocks(doc)).Single(x => x.Excerpt == "utilize");
            var me = Thread.CurrentThread.ManagedThreadId;
            Action<string> writing = p => { if (Thread.CurrentThread.ManagedThreadId == me) f.IsFixed = true; };
            ActionRunner.Writing += writing;
            try
            {
                Assert.Equal(0, runner.ApplyFixes(doc, new[] { f }, "fix"));
            }
            finally { ActionRunner.Writing -= writing; }
            Assert.Contains("utilize", doc.Document.OuterXml);
        }

        [Fact]
        public void Apply_BlockReplacement_RaisesTheEvents()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var runner = new ActionRunner(new FakeLlmClient(r => "<revised><p MadCap:conditions=\"Default.Print\">Only in print:</p><ul MadCap:conditions=\"Default.Print\"><li>output.</li></ul></revised>"), new PromptBuilder(null), 1024);
            var proposal = runner.Restructure(doc, runner.PickBlocks(doc), null);
            Assert.Null(proposal.WordPlan);
            var me = Thread.CurrentThread.ManagedThreadId;
            var writing = 0; var wrote = 0;
            Action<string> w1 = p => { if (Thread.CurrentThread.ManagedThreadId == me) writing++; };
            Action<string> w2 = p => { if (Thread.CurrentThread.ManagedThreadId == me) wrote++; };
            ActionRunner.Writing += w1;
            ActionRunner.Wrote += w2;
            try { runner.Apply(doc, proposal, "x"); }
            finally { ActionRunner.Writing -= w1; ActionRunner.Wrote -= w2; }
            Assert.Equal(1, writing);
            Assert.Equal(1, wrote);
        }

        [Fact]
        public void AltTextApply_RaisesTheEvents()
        {
            var path = Topic("<h2>Pay a bill</h2><p>The payee list: <img src=\"Images/payees.png\" /></p>");
            var doc = new FakeDocumentAdapter(path, true);
            var block = doc.Document.SelectSingleNode("//*[local-name()='p']");
            var me = Thread.CurrentThread.ManagedThreadId;
            var writing = 0; var wrote = 0;
            Action<string> w1 = p => { if (Thread.CurrentThread.ManagedThreadId == me) writing++; };
            Action<string> w2 = p => { if (Thread.CurrentThread.ManagedThreadId == me) wrote++; };
            ActionRunner.Writing += w1;
            ActionRunner.Wrote += w2;
            try { AltText.Apply(doc, block, "Images/payees.png", "The Payees list with three payees"); }
            finally { ActionRunner.Writing -= w1; ActionRunner.Wrote -= w2; }
            Assert.Equal(1, writing);
            Assert.Equal(1, wrote);
        }

        [Fact]
        public void Wrote_IsRaised_WhenTheWriteThrows()
        {
            var me = Thread.CurrentThread.ManagedThreadId;
            var wrote = 0;
            Action<string> w = p => { if (Thread.CurrentThread.ManagedThreadId == me) wrote++; };
            ActionRunner.Wrote += w;
            try
            {
                Assert.Throws<InvalidOperationException>(() => ActionRunner.WriteTracked("x", () => { throw new InvalidOperationException(); }));
            }
            finally { ActionRunner.Wrote -= w; }
            Assert.Equal(1, wrote);
        }

        [Fact]
        public void AHandlerThatThrows_DoesNotStopTheWrite()
        {
            var ran = false;
            Action<string> bad = p => { throw new InvalidOperationException("handler"); };
            ActionRunner.Writing += bad;
            try { ActionRunner.WriteTracked("x", () => ran = true); }
            finally { ActionRunner.Writing -= bad; }
            Assert.True(ran);
        }

        [Fact]
        public void LintFiles_RecordsBodyCounts()
        {
            var path = Topic("<p>We utilize it.</p><p>Then utilize more.</p>");
            var runner = Runner();
            var found = runner.LintFiles(new List<string> { path }).Where(f => f.Excerpt == "utilize").ToList();
            Assert.Equal(2, found.Count);
            foreach (var f in found)
            {
                Assert.Equal(2, f.SeenInBody);
                Assert.Equal(2, f.Seen);
            }
        }
    }
}
