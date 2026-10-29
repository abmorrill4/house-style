using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>One recorded decision (a line of a feedback .jsonl file).</summary>
    public sealed class Decision
    {
        public DateTime At { get; set; }
        public string Writer { get; set; }
        public string Verdict { get; set; }   // accepted, rejected, edited, refined, rule ignored, propose-ignore
        public string Reason { get; set; }
        public string Rule { get; set; }
        public string RuleId { get; set; }
        /// <summary>Fingerprint of the rule's wording when the decision was made (null in older logs).</summary>
        public string RuleHash { get; set; }
        public string Layer { get; set; }
        public string Excerpt { get; set; }
        public string Replacement { get; set; }
        public string File { get; set; }
        /// <summary>The rule a decision is about: its guide id when known, else the linter/AI rule name.</summary>
        public string Key { get { return !string.IsNullOrEmpty(RuleId) ? RuleId : (Layer == "ai" ? "ai: " : "") + (Rule ?? "?"); } }
        public bool IsAccept { get { return Verdict == "accepted" || Verdict == "edited"; } }
        public bool IsReject { get { return Verdict == "rejected" || Verdict == "rule ignored" || Verdict == "propose-ignore" || Verdict == "request"; } }
    }

    /// <summary>Something for the style-guide owner to look at, with its evidence.</summary>
    public sealed class TuneItem
    {
        /// <summary>"review" (often rejected), "disputed" (writers split), "edit" (same edit repeated), "ignore" (ignore requests), "working" (rarely rejected).</summary>
        public string Kind { get; set; }
        public string Rule { get; set; }
        public string Title { get; set; }
        public int Accepted { get; set; }
        public int Rejected { get; set; }
        public List<string> Writers { get; set; }
        public List<string> Evidence { get; set; }
    }

    /// <summary>
    /// Reads every writer's feedback and finds what the guide owner should act on. Thresholds keep one writer's taste
    /// from changing a shared rule: a rule is raised only on enough decisions from enough writers.
    /// </summary>
    public static class FeedbackStats
    {
        public static List<Decision> Load(IEnumerable<string> files)
        {
            var list = new List<Decision>();
            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;
                string[] lines;
                try { lines = ReadShared(file); } catch (Exception ex) { Diagnostics.Log("feedback " + file, ex); continue; }
                foreach (var line in lines)
                {
                    if (line.Trim().Length == 0) continue;
                    try
                    {
                        var o = JObject.Parse(line);
                        list.Add(new Decision
                        {
                            At = (DateTime?)o["at"] ?? DateTime.MinValue, Writer = (string)o["writer"] ?? Path.GetFileNameWithoutExtension(file),
                            Verdict = (string)o["decision"], Reason = (string)o["reason"], Rule = (string)o["rule"], RuleId = (string)o["rule_id"], RuleHash = (string)o["rule_hash"],
                            Layer = (string)o["layer"], Excerpt = (string)o["excerpt"], Replacement = (string)o["replacement"], File = (string)o["file"]
                        });
                    }
                    catch (Exception) { } // a torn last line while a writer appends: skip it
                }
            }
            return list;
        }

        /// <summary>
        /// For <c>attribution = counts</c>: replaces every writer id (including emails in older records, or in a legacy file's name)
        /// with "Writer 1", "Writer 2"… so views and reports can count writers without naming anyone.
        /// </summary>
        public static List<Decision> Anonymize(IEnumerable<Decision> decisions)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Decision>();
            foreach (var d in decisions)
            {
                var key = d.Writer ?? "";
                string alias;
                if (!map.TryGetValue(key, out alias)) { alias = "Writer " + (map.Count + 1); map[key] = alias; }
                result.Add(new Decision
                {
                    At = d.At, Writer = alias, Verdict = d.Verdict, Reason = d.Reason, Rule = d.Rule, RuleId = d.RuleId, RuleHash = d.RuleHash,
                    Layer = d.Layer, Excerpt = d.Excerpt, Replacement = d.Replacement, File = d.File
                });
            }
            return result;
        }

        /// <summary>"3 writers" / "1 writer".</summary>
        public static string WriterCount(int n) { return n + (n == 1 ? " writer" : " writers"); }

        /// <summary>The feedback files of a shared folder (one per writer).</summary>
        public static IEnumerable<string> FilesIn(string folder)
        {
            return string.IsNullOrEmpty(folder) || !Directory.Exists(folder) ? new string[0] : Directory.GetFiles(folder, "*.jsonl");
        }

        private static string[] ReadShared(string file)
        {
            // Other writers' sync clients may hold the file open: read without locking it.
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s)) return r.ReadToEnd().Split('\n');
        }

        /// <summary>
        /// A "restored" record cancels the latest earlier reject of the same issue (same writer, file, rule and excerpt) that is
        /// still standing, and is dropped itself: it counts neither as a reject nor as anything else. No match: nothing happens.
        /// </summary>
        private static List<Decision> CancelRestored(IEnumerable<Decision> decisions)
        {
            var kept = new List<Decision>();
            foreach (var d in decisions)
            {
                if (d.Verdict != "restored") { kept.Add(d); continue; }
                for (var i = kept.Count - 1; i >= 0; i--)
                {
                    var r = kept[i];
                    if (r.IsReject && StringComparer.OrdinalIgnoreCase.Equals(r.Writer, d.Writer) && r.File == d.File && r.Key == d.Key && r.Excerpt == d.Excerpt)
                    {
                        kept.RemoveAt(i);
                        break;
                    }
                }
            }
            return kept;
        }

        public static List<TuneItem> Analyze(IList<Decision> decisions, int minDecisions = 8, int minWriters = 2, double rejectRate = 0.4)
        {
            var items = new List<TuneItem>();
            decisions = CancelRestored(decisions);
            foreach (var g in decisions.Where(d => d.IsAccept || d.IsReject).GroupBy(d => d.Key))
            {
                // Only decisions about the rule as it reads now (the wording of its latest decision): a rewritten rule
                // starts fresh instead of carrying rejections of what it used to say.
                var latest = g.Where(d => d.RuleHash != null).OrderBy(d => d.At).Select(d => d.RuleHash).LastOrDefault();
                var all = g.Where(d => latest == null || d.RuleHash == null || d.RuleHash == latest).ToList();
                var earlier = g.Count() - all.Count;
                if (all.Count == 0) continue;
                int acc = all.Count(d => d.IsAccept), rej = all.Count(d => d.IsReject);
                var writers = all.Select(d => d.Writer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rate = (double)rej / Math.Max(1, acc + rej);

                // Writers split: someone mostly accepts it, someone else mostly rejects it.
                var perWriter = all.GroupBy(d => d.Writer, StringComparer.OrdinalIgnoreCase)
                    .Where(w => w.Count() >= 3)
                    .Select(w => new { Writer = w.Key, Rate = (double)w.Count(d => d.IsReject) / w.Count(), N = w.Count() }).ToList();
                var accepters = perWriter.Where(w => w.Rate <= 0.3).ToList();
                var rejecters = perWriter.Where(w => w.Rate >= 0.7).ToList();
                if (accepters.Count > 0 && rejecters.Count > 0)
                {
                    items.Add(new TuneItem
                    {
                        Kind = "disputed", Rule = g.Key, Title = "Writers disagree on " + g.Key, Accepted = acc, Rejected = rej, Writers = writers,
                        Evidence = accepters.Select(w => w.Writer + " changes it (" + Pct(1 - w.Rate) + " of " + w.N + ")")
                            .Concat(rejecters.Select(w => w.Writer + " ignores it (" + Pct(w.Rate) + " of " + w.N + ")"))
                            .Concat(Reasons(all)).Concat(Samples(all.Where(d => d.IsReject), 3)).ToList()
                    });
                }
                else if (acc + rej >= minDecisions && writers.Count >= minWriters && rate >= rejectRate)
                {
                    items.Add(new TuneItem
                    {
                        Kind = "review", Rule = g.Key, Title = "Often ignored: " + g.Key + " (" + Pct(rate) + " of " + (acc + rej) + ")",
                        Accepted = acc, Rejected = rej, Writers = writers,
                        Evidence = Reasons(all).Concat(Samples(all.Where(d => d.IsReject), 4))
                            .Concat(earlier > 0 ? new[] { Plural.Of(earlier, "older decision") + " about an earlier wording of this rule aren't counted." } : new string[0]).ToList()
                    });
                }
                else if (acc + rej >= minDecisions && rate <= 0.1)
                {
                    items.Add(new TuneItem
                    {
                        Kind = "working", Rule = g.Key, Title = "Working well: " + g.Key + " (" + Pct(1 - rate) + " accepted of " + (acc + rej) + ")",
                        Accepted = acc, Rejected = rej, Writers = writers, Evidence = new List<string>()
                    });
                }
            }

            // The same hand edit, again and again: the guide's replacement is probably wrong (a candidate Avoid/Use row).
            foreach (var g in decisions.Where(d => d.Verdict == "edited" && !string.IsNullOrEmpty(d.Excerpt))
                         .GroupBy(d => d.Excerpt.Trim().ToLowerInvariant() + " → " + (d.Replacement ?? "").Trim().ToLowerInvariant()))
            {
                var all = g.ToList();
                var writers = all.Select(d => d.Writer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (all.Count < 3 && writers.Count < 2) continue;
                items.Add(new TuneItem
                {
                    Kind = "edit", Rule = all[0].Key, Title = "Writers keep editing \"" + all[0].Excerpt + "\" to \"" + all[0].Replacement + "\" (" + all.Count + "×)",
                    Accepted = all.Count, Writers = writers, Evidence = new List<string> { "Rule: " + all[0].Key, "Candidate Avoid/Use row: avoid " + all[0].Excerpt + ", use " + all[0].Replacement }
                });
            }

            // Requests to turn a rule off (strict mode routes them to the owner).
            foreach (var g in decisions.Where(d => d.Verdict == "propose-ignore").GroupBy(d => d.Key))
            {
                var all = g.ToList();
                items.Add(new TuneItem
                {
                    Kind = "ignore", Rule = g.Key, Title = "Asked to disable: " + g.Key + " (" + Plural.Of(all.Count, "request") + ")",
                    Rejected = all.Count, Writers = all.Select(d => d.Writer).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Evidence = all.Select(d => d.Writer + ": " + (d.Reason ?? "(no reason)") + " — " + Path.GetFileName(d.File ?? "")).ToList()
                });
            }

            var order = new[] { "disputed", "review", "edit", "ignore", "working" };
            return items.OrderBy(i => Array.IndexOf(order, i.Kind)).ThenByDescending(i => i.Rejected + i.Accepted).ToList();
        }

        private static IEnumerable<string> Reasons(List<Decision> all)
        {
            return all.Where(d => d.IsReject && !string.IsNullOrEmpty(d.Reason)).GroupBy(d => d.Reason)
                .OrderByDescending(r => r.Count()).Take(3).Select(r => "Reason \"" + r.Key + "\": " + r.Count() + "×");
        }

        private static IEnumerable<string> Samples(IEnumerable<Decision> ds, int n)
        {
            return ds.Where(d => !string.IsNullOrEmpty(d.Excerpt)).GroupBy(d => d.Excerpt).Take(n)
                .Select(g => "\"" + g.Key + "\" (" + g.First().Writer + ", " + Path.GetFileName(g.First().File ?? "") + ")");
        }

        private static string Pct(double r) { return Math.Round(r * 100) + "%"; }
    }
}
