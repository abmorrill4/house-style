using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.TestKit.Conformance;
using FlareAI.TestKit.Editor;
using Xunit;

namespace FlareAI.Scenarios.Smoke
{
    /// <summary>
    /// The adapter conformance suite against FlareSimDocument: one test per documented Flare 22 write behaviour. The
    /// same cases (Kind = Flare) run inside Flare against FlareDocumentAdapter, so the fake stays honest.
    /// </summary>
    public class FlareSimConformanceTests
    {
        public static IEnumerable<object[]> Cases { get { return AdapterConformance.Cases.Select(c => new object[] { c.Name }); } }

        [Theory]
        [MemberData(nameof(Cases))]
        public void FlareSim_Conforms(string name)
        {
            AdapterConformance.Find(name).Run(xml => FlareSimDocument.FromXml(xml));
        }

        [Fact]
        public void DirectDomWrites_AreReportedAsUntracked()
        {
            var doc = FlareSimDocument.FromXml(AdapterConformance.Topic);
            doc.Edit("sneaky", () => AdapterConformance.Block(doc, 0).SetAttribute("class", "x"));
            Assert.Single(doc.UntrackedWrites);
            Assert.Empty(FlareSimDocument.FromXml(AdapterConformance.Topic).UntrackedWrites);
        }

        [Fact]
        public void DetachedFragments_AreNotWrites()
        {
            var doc = FlareSimDocument.FromXml(AdapterConformance.Topic);
            var p = doc.Document.CreateElement("p");
            p.AppendChild(doc.Document.CreateTextNode("Built off-tree."));
            p.SetAttribute("class", "tip");
            Assert.Empty(doc.UntrackedWrites);
        }

        [Fact]
        public void AcceptAll_IsItselfOneUndoStep()
        {
            var doc = FlareSimDocument.FromXml(AdapterConformance.Topic);
            doc.Edit("fix", () => AdapterConformance.Replace(doc, 0, "Check", "Verify"));
            var tracked = doc.Xml;
            doc.AcceptAll();
            Assert.Equal(2, doc.UndoCount);
            doc.Undo();
            Assert.Equal(tracked, doc.Xml);
        }

        [Fact]
        public void ChangeData_UsesTheFlareShape()
        {
            var doc = FlareSimDocument.FromXml(AdapterConformance.Topic);
            doc.UserName = "pat";
            doc.Initials = "PA";
            doc.Edit("fix", () => AdapterConformance.Replace(doc, 0, "Check", "Verify"));
            var ns = new XmlNamespaceManager(doc.Document.NameTable);
            ns.AddNamespace("MadCap", FlareSimDocument.MadCapNs);
            var data = doc.Document.SelectSingleNode("/html/head/MadCap:changeData", ns);
            Assert.NotNull(data);
            Assert.Same(data, doc.Document.SelectSingleNode("/html/head", ns).FirstChild);
            var add = (XmlElement)data.SelectSingleNode("MadCap:AddChange", ns);
            Assert.Equal("pat", add.GetAttribute("userName", FlareSimDocument.MadCapNs));
            Assert.Equal("PA", add.GetAttribute("initials", FlareSimDocument.MadCapNs));
            Assert.NotEmpty(add.GetAttribute("timestamp", FlareSimDocument.MadCapNs));
        }

        [Fact]
        public void NewIds_FollowIdsAlreadyInTheTopic()
        {
            var doc = FlareSimDocument.FromFile(System.IO.Path.Combine(FlareAI.TestKit.Workspace.Repo.Fixtures, "tracked-changes-topic.htm"));
            doc.Edit("fix", () => Assert.True(AdapterConformance.Replace(doc, 1, "print output", "printed output")));
            var ids = new List<string>();
            foreach (XmlElement e in doc.Document.GetElementsByTagName("AddChange", FlareSimDocument.MadCapNs)) ids.Add(e.GetAttribute("id", FlareSimDocument.MadCapNs));
            Assert.Equal(new[] { "2", "4" }, ids.ToArray());
        }
    }
}
