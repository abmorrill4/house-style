using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FlareAI.Core.Style;
using FlareAI.FixtureGen;

namespace FlareAI.RuleFixtures.Tests
{
    /// <summary>One failed check. The id names the check, the rule, the context and the case.</summary>
    public sealed class Failure
    {
        public string Check, RuleId, Context, Cause;
        public int N;
        public string Tags = "";
        public string Id { get { return Check + "|" + RuleId + "|" + Context + "|" + N; } }
        public override string ToString() { return Id + "  [" + Cause + "]"; }
    }

    public sealed class SuiteResult
    {
        public List<Failure> Failures = new List<Failure>();
        public int Checks, Passed, Inconclusive;
        public int RulesChecked;
        public TimeSpan Elapsed;
        public Dictionary<string, int> ChecksByKind = new Dictionary<string, int>();
        public Dictionary<string, int> FailuresByKind = new Dictionary<string, int>();
    }

    /// <summary>
    /// Runs every fixture through the real pipeline (template -> TextLines -> Composition.Lint) and records what fails.
    /// The expected outcomes are in the fixtures (from expectations.yaml); nothing here knows which rules are buggy.
    /// </summary>
    public static class Suite
    {
        private static readonly Lazy<SuiteResult> _result = new Lazy<SuiteResult>(Run);
        public static SuiteResult Result { get { return _result.Value; } }

        private sealed class Line { public LintLine Input; public string Text { get { return Input.Text; } } }

        private sealed class Placed { public int Line = -1; public int Start; public string Text; }

        // ---- the only place that talks to the engine's lint API ------------------------------------------------------
        // Lint takes lines with their context (#53): where the line sits and its inline spans, as TextLines extracts them
        // from the rendered template. Identical lines in identical contexts are linted once.

        private sealed class Linted
        {
            private readonly List<Line> _lines;
            private readonly Dictionary<string, int> _unique = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<int> _map = new List<int>();
            private readonly ILookup<int, LintHit> _hits;

            public Linted(Composition comp, List<Line> lines, bool all = false)
            {
                _lines = lines;
                var inputs = new List<LintLine>();
                foreach (var l in lines)
                {
                    var key = (int)l.Input.Context + "|" + string.Join(",", l.Input.Spans.Select(x => x.Start + ":" + x.Length + ":" + (int)x.Context).ToArray()) + "|" + l.Text;
                    int u;
                    if (!_unique.TryGetValue(key, out u)) { u = inputs.Count; _unique[key] = u; inputs.Add(l.Input); }
                    _map.Add(u);
                }
                _hits = comp.Lint(inputs, all).ToLookup(h => h.Index);
            }

            public List<LintHit> For(int line) { return _hits[_map[line]].ToList(); }
        }

        // ---- rendering a case in a context -----------------------------------------------------------------------------

        private const string Marker = "ZQXCASEQXZ";
        internal sealed class Frame { public string Prefix, Suffix; public bool Heading; public LintLine Input; }
        private static readonly Dictionary<string, Frame> Frames = new Dictionary<string, Frame>(StringComparer.Ordinal);

        /// <summary>The line the engine builds around a case in this context, found once with a marker; null when the engine drops the text (code, pre, kbd).</summary>
        internal static Frame FrameOf(ContextSet ctx, string context)
        {
            Frame f;
            if (Frames.TryGetValue(context, out f)) return f;
            foreach (var l in TextLines.Of(ctx.Render(context, Marker), ContextSet.VariableValue))
            {
                var at = l.Text.IndexOf(Marker, StringComparison.Ordinal);
                if (at < 0) continue;
                f = new Frame { Prefix = l.Text.Substring(0, at), Suffix = l.Text.Substring(at + Marker.Length), Heading = l.IsHeading, Input = l.ToLint() };
                break;
            }
            Frames[context] = f;
            return f;
        }

        private static Placed Place(ContextSet ctx, string context, string text, List<Line> lines)
        {
            var p = new Placed { Text = text };
            var frame = FrameOf(ctx, context);
            if (frame == null) return p;   // Line stays -1: nothing is linted
            p.Line = lines.Count; p.Start = frame.Prefix.Length;
            // The case replaces the marker: spans after it shift, a span around it (the label, the link) stretches over the case.
            var at = frame.Prefix.Length; var d = text.Length - Marker.Length;
            var input = new LintLine { Text = (frame.Prefix + text + frame.Suffix).Replace('\r', ' ').Replace('\n', ' '), Context = frame.Input.Context, Known = frame.Input.Known };
            foreach (var x in frame.Input.Spans)
            {
                int s = x.Start >= at + Marker.Length ? x.Start + d : x.Start, e = x.Start + x.Length > at ? x.Start + x.Length + d : x.Start + x.Length;
                input.Spans.Add(new ContextSpan { Start = s, Length = e - s, Context = x.Context });
            }
            lines.Add(new Line { Input = input });
            return p;
        }

        private static List<int[]> OwnSpans(LintRule rule, string text)
        {
            var spans = new List<int[]>();
            foreach (var s in rule.Swaps)
                foreach (Match m in s.Key.Matches(text))
                    if (m.Length > 0) spans.Add(new[] { m.Index, m.Index + m.Length });
            return spans;
        }

        private static bool Overlaps(LintHit h, int start, int end) { return h.Start < end && start < h.Start + h.Length; }
        private static bool Overlaps(LintHit h, List<int[]> spans) { return spans.Any(s => Overlaps(h, s[0], s[1])); }

        /// <summary>Rule ids the finding says it also stands for: every other rule on the same words, which Turn off offers with it (#54).</summary>
        private static IEnumerable<string> Also(LintHit h) { return h.AlsoRules.Select(r => r.Id); }

        private static bool Stands(LintHit h, string ruleId) { return h.RuleId == ruleId || Also(h).Contains(ruleId); }

        private static string Sig(IEnumerable<LintHit> hits) { return string.Join(";", hits.OrderBy(h => h.Index).ThenBy(h => h.Start).ThenBy(h => h.Length).Select(h => h.Index + ":" + h.Start + ":" + h.Length + ":" + h.RuleId).ToArray()); }

        // ---- the run ---------------------------------------------------------------------------------------------

        private static SuiteResult Run()
        {
            var sw = Stopwatch.StartNew();
            var res = new SuiteResult();
            var fixtures = Repo.LoadRules();
            var guide = GuideSet.Load(Repo.Guide);
            var comp = guide.Compose();
            var ctx = ContextSet.Load(Path.Combine(Repo.GenDir, "templates"));
            var byId = comp.Rules.Where(r => r.Id != null).ToDictionary(r => r.Id, r => r);

            var tmp = Path.Combine(Path.GetTempPath(), "flareai-rulefixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tmp, "Content"));
            File.WriteAllText(Path.Combine(tmp, "Fixture.flprj"), "<CatapultProject />");
            try
            {
                var project = GuideLayer.Load(tmp, "project");
                var paragraphs = new Dictionary<string, List<string>>();   // rule -> paragraph texts, for the reload comparison
                var signatures = new Dictionary<string, string>();

                foreach (var f in fixtures)
                {
                    Func<string, string, int, string, bool, bool> record = (check, context, n, cause, ok) =>
                    {
                        res.Checks++;
                        int c; res.ChecksByKind.TryGetValue(check, out c); res.ChecksByKind[check] = c + 1;
                        if (ok) { res.Passed++; return true; }
                        res.FailuresByKind.TryGetValue(check, out c); res.FailuresByKind[check] = c + 1;
                        res.Failures.Add(new Failure { Check = check, RuleId = f.Id, Context = context, N = n, Cause = cause, Tags = string.Join("+", f.Tags.ToArray()) });
                        return false;
                    };

                    LintRule rule;
                    if (!byId.TryGetValue(f.Id, out rule))
                    {
                        // A row the engine skips on load (#55) is fine; otherwise the fixtures are stale.
                        record("rule-missing", "-", 0, "the engine has no rule " + f.Id, f.MarkupOnly);
                        continue;
                    }
                    res.RulesChecked++;
                    record("pattern-plain", "-", 0, "a pattern matches markup only: " + string.Join(" | ", rule.Swaps.Select(s => s.Key.ToString()).Where(RuleShape.MatchesMarkup).Take(1).ToArray()),
                           !rule.Swaps.Any(s => RuleShape.MatchesMarkup(s.Key.ToString())));
                    if (f.MarkupOnly) continue;

                    // -- render every case in every context, lint them all at once ------------------------------------------------
                    var lines = new List<Line>();
                    var badPlaced = new List<KeyValuePair<string, Placed>>[f.Bad.Count];
                    for (var n = 0; n < f.Bad.Count; n++)
                    {
                        badPlaced[n] = new List<KeyValuePair<string, Placed>>();
                        foreach (var kv in f.Bad[n].ByContext().Select(x => x.Key).Distinct())
                            badPlaced[n].Add(new KeyValuePair<string, Placed>(kv, Place(ctx, kv, f.Bad[n].Text, lines)));
                    }
                    var goodPlaced = new List<KeyValuePair<string, Placed>>[f.Good.Count];
                    for (var n = 0; n < f.Good.Count; n++)
                    {
                        goodPlaced[n] = new List<KeyValuePair<string, Placed>>();
                        foreach (var c in f.Good[n].ContextList()) goodPlaced[n].Add(new KeyValuePair<string, Placed>(c, Place(ctx, c, f.Good[n].Text, lines)));
                    }
                    var linted = new Linted(comp, lines);

                    // -- bad cases: fires / clean per context -----------------------------------------------------------------
                    for (var n = 0; n < f.Bad.Count; n++)
                    {
                        var expect = f.Bad[n].ByContext().ToDictionary(x => x.Key, x => x.Value);
                        foreach (var kv in badPlaced[n])
                        {
                            var outcome = expect[kv.Key];
                            if (outcome == Outcome.Any) continue;
                            var p = kv.Value;
                            if (p.Line < 0)
                            {
                                if (outcome == Outcome.Fires) record("bad-fires", kv.Key, n, "the text is not linted in this context", false);
                                else record("bad-clean", kv.Key, n, "", true);
                                continue;
                            }
                            var own = OwnSpans(rule, lines[p.Line].Text);
                            if (own.Count == 0) { res.Inconclusive++; continue; }   // the template changed the text so the row's own pattern no longer matches
                            var shown = linted.For(p.Line);
                            if (outcome == Outcome.Fires)
                            {
                                var ok = shown.Any(h => Stands(h, f.Id) && Overlaps(h, own));
                                var other = shown.FirstOrDefault(h => Overlaps(h, own));
                                record("bad-fires", kv.Key, n, ok ? "" : other != null ? "shadowed by " + (other.RuleId ?? other.Rule) : "not flagged", ok);
                            }
                            else
                            {
                                var hit = shown.FirstOrDefault(h => h.RuleId == f.Id && Overlaps(h, own));
                                record("bad-clean", kv.Key, n, hit == null ? "" : "flagged out of scope", hit == null);
                            }
                        }
                    }

                    // -- good cases: no rule may flag the Use wording ------------------------------------------------------------
                    for (var n = 0; n < f.Good.Count; n++)
                        foreach (var kv in goodPlaced[n])
                        {
                            var p = kv.Value;
                            if (p.Line < 0) { record("good-clean", kv.Key, n, "", true); continue; }
                            var hit = linted.For(p.Line).FirstOrDefault(h => Overlaps(h, p.Start, p.Start + p.Text.Length));
                            record("good-clean", kv.Key, n, hit == null ? "" : (hit.RuleId == f.Id ? "its own rule flags it" : "flagged by " + (hit.RuleId ?? hit.Rule)), hit == null);
                        }

                    // -- paragraph cases: fix, turn off, stable ------------------------------------------------------------------------
                    var para = new List<KeyValuePair<int, string>>();   // bad index -> text, for cases that have a paragraph context
                    for (var n = 0; n < f.Bad.Count; n++) if (f.Bad[n].ByContext().Any(x => x.Key == "paragraph")) para.Add(new KeyValuePair<int, string>(n, f.Bad[n].Text));
                    paragraphs[f.Id] = para.Select(x => x.Value).ToList();

                    // fix: the rule's own replacement leaves nothing new behind (#59)
                    var primary = para.FirstOrDefault();
                    if (primary.Value != null && f.Bad[primary.Key].Source == "example")
                    {
                        var all = comp.Lint(new[] { primary.Value }, null, true).Where(h => h.RuleId == f.Id).ToList();
                        if (all.Count == 0) res.Inconclusive++;
                        else if (all.Any(h => h.Replacement == null)) res.Inconclusive++;   // flag-only: the writer decides
                        else
                        {
                            var fixedText = primary.Value;
                            var chosen = new List<LintHit>();
                            foreach (var h in all.OrderByDescending(h => h.Length))
                                if (!chosen.Any(c => h.Start < c.Start + c.Length && c.Start < h.Start + h.Length)) chosen.Add(h);
                            foreach (var h in chosen.OrderByDescending(h => h.Start))
                                fixedText = fixedText.Substring(0, h.Start) + h.Replacement + fixedText.Substring(h.Start + h.Length);
                            var before = comp.Lint(new[] { primary.Value }, null);
                            var after = comp.Lint(new[] { fixedText }, null);
                            var self = after.FirstOrDefault(h => h.RuleId == f.Id);
                            var created = after.FirstOrDefault(h => h.RuleId != f.Id && !before.Any(b => b.RuleId == h.RuleId && b.Match == h.Match));
                            record("fix-clean", "paragraph", primary.Key,
                                   self != null ? "the fix does not resolve the rule: '" + fixedText + "'" : created != null ? "the fix creates a finding from " + (created.RuleId ?? created.Rule) + ": '" + fixedText + "'" : "",
                                   self == null && created == null);
                        }
                    }

                    // turn off at project and team level: no hit may remain on the span unless the shown finding lists it (#34, #54)
                    if (para.Count > 0)
                    {
                        var texts = para.Select(x => x.Value).ToList();
                        var baseHits = comp.Lint(texts, null);
                        for (var mode = 0; mode < 2; mode++)
                        {
                            Composition off;
                            if (mode == 0) { project.Overrides.Clear(); project.Overrides.Add(new KeyValuePair<string, string>(f.Id, "off")); off = guide.Compose(project); project.Overrides.Clear(); }
                            else { guide.Company.Overrides.Add(new KeyValuePair<string, string>(f.Id, "off")); off = guide.Compose(); guide.Company.Overrides.Clear(); }
                            var offHits = off.Lint(texts, null);
                            for (var i = 0; i < texts.Count; i++)
                            {
                                var own = OwnSpans(rule, texts[i]);
                                if (own.Count == 0) { res.Inconclusive++; continue; }
                                var exposed = offHits.Where(h => h.Index == i && Overlaps(h, own)).ToList();
                                // The rules on the span before turning off: each shown finding and every rule it lists (Turn off offers them all).
                                var listed = baseHits.Where(h => h.Index == i && Overlaps(h, own)).SelectMany(h => h.RuleIds).ToList();
                                var bad = exposed.FirstOrDefault(h => !listed.Contains(h.RuleId ?? h.Rule));
                                record(mode == 0 ? "off-project" : "off-team", "paragraph", para[i].Key,
                                       bad == null ? "" : bad.RuleId == f.Id ? "still flagged by the rule itself" : "still flagged by " + (bad.RuleId ?? bad.Rule), bad == null);
                            }
                        }

                        // same rule shown on every run (#54)
                        var first = Sig(baseHits);
                        signatures[f.Id] = first;
                        var run2 = comp.Lint(texts, null); var run3 = comp.Lint(texts, null);
                        for (var i = 0; i < texts.Count; i++)
                        {
                            var idx = i;
                            Func<IList<LintHit>, string> mine = hs => Sig(hs.Where(h => h.Index == idx));
                            var same = mine(run2) == mine(baseHits) && mine(run3) == mine(baseHits);
                            record("stable", "paragraph", para[i].Key, same ? "" : "another run shows different rules", same);
                        }
                    }
                }

                // reload: a new Composition from freshly parsed layers shows the same rules
                var guide2 = GuideSet.Load(Repo.Guide);
                var comp2 = guide2.Compose();
                foreach (var f in fixtures)
                {
                    string first;
                    if (!signatures.TryGetValue(f.Id, out first)) continue;
                    var again = Sig(comp2.Lint(paragraphs[f.Id], null));
                    res.Checks++; int c; res.ChecksByKind.TryGetValue("reload", out c); res.ChecksByKind["reload"] = c + 1;
                    if (again == first) res.Passed++;
                    else
                    {
                        res.FailuresByKind.TryGetValue("reload", out c); res.FailuresByKind["reload"] = c + 1;
                        res.Failures.Add(new Failure { Check = "reload", RuleId = f.Id, Context = "paragraph", N = 0, Cause = "after reloading the guide the shown rules differ", Tags = string.Join("+", f.Tags.ToArray()) });
                    }
                }
                StructureCases(res);
            }
            finally { try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            res.Elapsed = sw.Elapsed;
            return res;
        }

        private static void StructureCases(SuiteResult res)
        {
            foreach (var rule in Repo.LoadStructure().Rules)
                for (var kind = 0; kind < 2; kind++)
                {
                    var cases = kind == 0 ? rule.Bad : rule.Good;
                    var check = kind == 0 ? "structure-bad" : "structure-good";
                    for (var n = 0; n < cases.Count; n++)
                    {
                        var found = StructureCheck.Of(ContextSet.Body(cases[n].Xhtml));
                        var ok = kind == 0 ? found.Any(x => x.Rule == rule.Rule) : found.Count == 0;
                        res.Checks++; int c; res.ChecksByKind.TryGetValue(check, out c); res.ChecksByKind[check] = c + 1;
                        if (ok) { res.Passed++; continue; }
                        res.FailuresByKind.TryGetValue(check, out c); res.FailuresByKind[check] = c + 1;
                        res.Failures.Add(new Failure
                        {
                            Check = check, RuleId = rule.Rule, Context = cases[n].Context, N = n, Tags = "Structure",
                            Cause = kind == 0 ? "not reported" : "reported: " + string.Join(", ", found.Select(x => x.Rule).Distinct().ToArray())
                        });
                    }
                }
        }
    }
}
