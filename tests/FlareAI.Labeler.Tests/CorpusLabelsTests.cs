using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Scoring;
using Xunit;

namespace FlareAI.Labeler.Tests
{
    /// <summary>The checked-in v2 corpus must keep passing the QA that gates it.</summary>
    public class RealCorpusTests
    {
        private static IEnumerable<string> Projects()
        {
            string v2 = CorpusProject.FindUp(AppDomain.CurrentDomain.BaseDirectory, "corpus/v2");
            return Directory.GetDirectories(v2).Where(d => File.Exists(Path.Combine(d, "labels.jsonl")));
        }

        [Fact]
        public void Every_v2_project_passes_label_qa_without_errors()
        {
            string defects = CorpusProject.FindUp(AppDomain.CurrentDomain.BaseDirectory, "corpus/schema/defects.yaml");
            string dbPath = CorpusProject.FindUp(AppDomain.CurrentDomain.BaseDirectory, "corpus/DigitalBanking/labels.jsonl");
            var tax = Taxonomy.Load(defects);
            var reference = LabelIO.Read(dbPath);
            var failures = new List<string>();
            foreach (var dir in Projects())
            {
                var project = CorpusProject.Open(dir);
                var rep = LabelQa.Run(project, project.ReadLabels(), tax, reference);
                failures.AddRange(rep.Issues.Where(i => i.Severity == "error").Take(5).Select(i => project.Name + " " + i.Code + " " + i.LabelId + ": " + i.Message));
            }
            Assert.Empty(failures);
        }

        [Fact]
        public void Normalizing_the_checked_in_labels_changes_nothing()
        {
            string defects = CorpusProject.FindUp(AppDomain.CurrentDomain.BaseDirectory, "corpus/schema/defects.yaml");
            var tax = Taxonomy.Load(defects);
            foreach (var dir in Projects())
            {
                var project = CorpusProject.Open(dir);
                var res = Normalizer.Run(project.ReadLabels(), tax, project);
                Assert.True(res.RulesRewritten == 0 && res.Promoted == 0, project.Name + ": rewritten " + res.RulesRewritten + ", promoted " + res.Promoted);
            }
        }
    }
}
