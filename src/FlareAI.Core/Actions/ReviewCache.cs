using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// AI review results keyed by a hash of the exact review prompt (fragment + lint summary + nothing else).
    /// Same content → same key, regardless of file name or path, so results survive renames and topic switches
    /// and an unchanged topic never pays for a second review. Holds raw (unanchored) findings; callers anchor
    /// copies against the live DOM. With a path, the cache is saved to disk, so reviews also survive closing Flare.
    /// <para>
    /// After the author applies the review's own fixes (or the linter's), the unit's prompt changes and the exact key
    /// misses. <see cref="Get(ReviewUnit)"/> then re-anchors instead of re-reviewing: when every change to the unit's
    /// text is one of those fixes, the findings not applied are still the review's answer for the new text. Re-check
    /// after applying fixes hit 23% of units before (measured); each re-anchored unit saves a review call.
    /// </para>
    /// </summary>
    public sealed class ReviewCache
    {
        private readonly int _capacity;
        private readonly string _path;
        private readonly Dictionary<string, List<StyleFinding>> _items = new Dictionary<string, List<StyleFinding>>(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private readonly object _gate = new object();
        // In memory only: re-anchoring serves re-checks in the same session.
        private readonly Dictionary<string, Shape> _shapes = new Dictionary<string, Shape>(StringComparer.Ordinal);

        /// <summary>
        /// A unit's text as the author reads it, cut at markup: adjacent runs are one segment, because an applied fix is a
        /// tracked change that splits a run into several ("We " + "use" + " the app") while the text reads the same.
        /// </summary>
        private sealed class Layout
        {
            /// <summary>The markup between segments; fixes change text, never this.</summary>
            public string Skeleton;
            public List<string> Texts = new List<string>();
            public List<List<string>> Runs = new List<List<string>>();
            public Dictionary<string, int> SegmentOf = new Dictionary<string, int>(StringComparer.Ordinal);

            public static Layout Of(RunMap map)
            {
                var l = new Layout();
                var sk = new StringBuilder();
                var at = 0;
                foreach (Match m in Regex.Matches(map.Marked ?? "", @"⟦(r\d+)⟧(.*?)⟦/\1⟧", RegexOptions.Singleline))
                {
                    var gap = map.Marked.Substring(at, m.Index - at);
                    at = m.Index + m.Length;
                    var id = m.Groups[1].Value;
                    string text;
                    if (!map.Texts.TryGetValue(id, out text)) text = m.Groups[2].Value;
                    if (gap.Length > 0 || l.Texts.Count == 0)
                    {
                        sk.Append(gap).Append("⟦t⟧");
                        l.Texts.Add("");
                        l.Runs.Add(new List<string>());
                    }
                    var i = l.Texts.Count - 1;
                    l.Texts[i] += text;
                    l.Runs[i].Add(id);
                    l.SegmentOf[id] = i;
                }
                sk.Append((map.Marked ?? "").Substring(at));
                l.Skeleton = sk.ToString();
                return l;
            }
        }

        private sealed class Shape
        {
            public Layout Layout;
            /// <summary>Lint fixes on offer when the unit was reviewed.</summary>
            public List<Fix> LintFixes;
            public long Stamp;
        }

        private sealed class Fix
        {
            public int Segment;
            public string Excerpt, Replacement;
            public StyleFinding Finding;   // the AI finding it comes from; null for a lint fix
        }

        private long _clock;

        /// <param name="path">JSON file to load from and save to (the host uses <see cref="Paths.Reviews"/>); null keeps the cache in memory only.</param>
        public ReviewCache(int capacity = 300, string path = null)
        {
            _capacity = capacity;
            _path = path;
            if (path == null || !File.Exists(path)) return;
            try
            {
                var saved = JsonConvert.DeserializeObject<List<KeyValuePair<string, List<StyleFinding>>>>(File.ReadAllText(path, Encoding.UTF8));
                if (saved != null) foreach (var kv in saved) if (kv.Value != null) { _items[kv.Key] = kv.Value; _order.AddLast(kv.Key); }
            }
            catch (Exception ex) { Diagnostics.Log("review cache " + Diagnostics.Path(path), ex); }
        }

        public static string Key(string prompt)
        {
            using (var sha = SHA1.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(prompt ?? "")));
        }

        /// <summary>The note the review pane shows when AI is off and earlier AI reviews of the text were hidden.</summary>
        public const string AiOffNote = "AI is off. Earlier AI issues are hidden.";

        /// <summary>
        /// The cached review to show for <paramref name="prompt"/>: none while AI is off (the writer chose no AI, so earlier AI
        /// suggestions aren't shown), and then <paramref name="hidden"/> says whether there was one.
        /// </summary>
        public List<StyleFinding> Shown(string prompt, bool aiOff, out bool hidden)
        {
            var raw = Get(prompt);
            hidden = aiOff && raw != null;
            return aiOff ? null : raw;
        }

        /// <summary><see cref="Shown(string, bool, out bool)"/> for a unit, with <see cref="Get(ReviewUnit)"/>'s re-anchoring.</summary>
        public List<StyleFinding> Shown(ReviewUnit unit, bool aiOff, out bool hidden)
        {
            return Shown(unit, aiOff, false, out hidden);
        }

        /// <summary>
        /// <see cref="Shown(ReviewUnit, bool, out bool)"/>; with <paramref name="fresh"/> (the writer pressed Check again) a unit
        /// whose current text was already reviewed is not served from the cache, so it gets a new review. A unit changed only
        /// by applied fixes is still re-anchored (<see cref="Get(ReviewUnit, bool)"/>).
        /// </summary>
        public List<StyleFinding> Shown(ReviewUnit unit, bool aiOff, bool fresh, out bool hidden)
        {
            if (aiOff) { var had = Get(unit); hidden = had != null; return null; }
            hidden = false;
            return Get(unit, fresh);
        }

        /// <summary>
        /// <see cref="Get(ReviewUnit)"/>, or with <paramref name="fresh"/> null for a unit whose exact text is in the cache (the
        /// writer asked for a new review of unchanged text; <see cref="Put(ReviewUnit, List{StyleFinding})"/> of the new review
        /// replaces the entry, and a failed call keeps the old one). A unit whose text changed only through applied fixes is
        /// still re-anchored without a call: its review is current.
        /// </summary>
        public List<StyleFinding> Get(ReviewUnit unit, bool fresh)
        {
            if (unit == null) return null;
            if (fresh)
                lock (_gate)
                    if (_items.ContainsKey(Key(unit.Prompt))) return null;
            return Get(unit);
        }

        /// <summary>Copies of the cached raw findings, or null.</summary>
        public List<StyleFinding> Get(string prompt)
        {
            lock (_gate)
            {
                List<StyleFinding> raw;
                return _items.TryGetValue(Key(prompt), out raw) ? raw.ConvertAll(Copy) : null;
            }
        }

        /// <summary>
        /// Copies of the unit's cached findings: an exact hit, or the findings of an earlier review of this unit re-anchored
        /// when the only changes since are fixes it (or the linter) proposed. Null = review it.
        /// </summary>
        public List<StyleFinding> Get(ReviewUnit unit)
        {
            if (unit == null) return null;
            var hit = Get(unit.Prompt);
            if (hit != null || unit.Runs == null) return hit;
            var now = Layout.Of(unit.Runs);
            List<StyleFinding> remaining = null;
            lock (_gate)
            {
                foreach (var kv in _shapes.Where(x => x.Value.Layout.Skeleton == now.Skeleton).OrderByDescending(x => x.Value.Stamp).ToList())
                {
                    List<StyleFinding> raw;
                    if (!_items.TryGetValue(kv.Key, out raw)) continue;
                    remaining = Explain(kv.Value, raw, now, unit.Runs);
                    if (remaining != null) break;
                }
            }
            if (remaining == null) return null;
            Diagnostics.Log("Review cache: re-anchored " + remaining.Count + " finding(s) after applied fixes; no review call");
            Put(unit, remaining);
            return remaining.ConvertAll(Copy);
        }

        public void Put(string prompt, List<StyleFinding> raw)
        {
            var key = Key(prompt);
            lock (_gate)
            {
                if (!_items.ContainsKey(key))
                {
                    _order.AddLast(key);
                    if (_order.Count > _capacity) { var old = _order.First.Value; _items.Remove(old); _shapes.Remove(old); _order.RemoveFirst(); }
                }
                _items[key] = raw.ConvertAll(Copy);
                Save();
            }
        }

        /// <summary>Caches the unit's findings by its prompt, and remembers its shape so a re-check after fixes can re-anchor.</summary>
        public void Put(ReviewUnit unit, List<StyleFinding> raw)
        {
            Put(unit.Prompt, raw);
            if (unit.Runs == null) return;
            var shape = new Shape { Layout = Layout.Of(unit.Runs), LintFixes = new List<Fix>() };
            foreach (var f in unit.Lint ?? new List<StyleFinding>())
            {
                if (f.Replacement == null || string.IsNullOrEmpty(f.Excerpt)) continue;
                var seg = shape.Layout.Texts.FindIndex(t => t.IndexOf(f.Excerpt, StringComparison.Ordinal) >= 0);
                if (seg >= 0) shape.LintFixes.Add(new Fix { Segment = seg, Excerpt = f.Excerpt, Replacement = f.Replacement });
            }
            lock (_gate)
            {
                shape.Stamp = ++_clock;
                _shapes[Key(unit.Prompt)] = shape;
            }
        }

        /// <summary>
        /// The findings still open when every changed segment is its old text with some of the offered fixes applied, each
        /// moved to the run that now holds its text; null when a change isn't explained that way (the author wrote something
        /// else: review it again).
        /// </summary>
        private static List<StyleFinding> Explain(Shape shape, List<StyleFinding> raw, Layout now, RunMap map)
        {
            var was = shape.Layout;
            if (now.Texts.Count != was.Texts.Count) return null;
            Func<StyleFinding, int> segmentOf = f => { int i; return f.RunId != null && was.SegmentOf.TryGetValue(f.RunId, out i) ? i : -1; };
            var applied = new HashSet<StyleFinding>();
            for (var i = 0; i < now.Texts.Count; i++)
            {
                if (was.Texts[i] == now.Texts[i]) continue;
                var seg = i;
                var offered = raw.Where(f => segmentOf(f) == seg && f.Replacement != null && !string.IsNullOrEmpty(f.Excerpt))
                                 .Select(f => new Fix { Segment = seg, Excerpt = f.Excerpt, Replacement = f.Replacement, Finding = f })
                                 .Concat(shape.LintFixes.Where(x => x.Segment == seg)).Take(10).ToList();
                var subset = Matching(was.Texts[i], now.Texts[i], offered);
                if (subset == null) return null;
                foreach (var x in subset) if (x.Finding != null) applied.Add(x.Finding);
            }
            var kept = new List<StyleFinding>();
            foreach (var f in raw)
            {
                var seg = segmentOf(f);
                if (applied.Contains(f) || seg < 0) continue;
                // The run that now holds the excerpt (a fix nearby may have split the old run).
                var run = now.Runs[seg].FirstOrDefault(id => map.Texts[id].IndexOf(f.Excerpt ?? "", StringComparison.Ordinal) >= 0);
                if (run == null) continue;
                var c = Copy(f);
                c.RunId = run;
                kept.Add(c);
            }
            return kept;
        }

        /// <summary>The fixes which, applied to <paramref name="old"/>, give <paramref name="now"/>; null when none do.</summary>
        private static List<Fix> Matching(string old, string now, List<Fix> offered)
        {
            for (var mask = 1; mask < (1 << offered.Count); mask++)
            {
                var pick = new List<Fix>();
                for (var b = 0; b < offered.Count; b++) if ((mask & (1 << b)) != 0) pick.Add(offered[b]);
                var spans = pick.Select(x => new { Fix = x, At = old.IndexOf(x.Excerpt, StringComparison.Ordinal) }).OrderByDescending(x => x.At).ToList();
                if (spans.Any(x => x.At < 0)) continue;
                var ok = true;
                for (var i = 1; i < spans.Count; i++) if (spans[i].At + spans[i].Fix.Excerpt.Length > spans[i - 1].At) ok = false;   // overlapping
                if (!ok) continue;
                var text = old;
                foreach (var x in spans) text = text.Substring(0, x.At) + x.Fix.Replacement + text.Substring(x.At + x.Fix.Excerpt.Length);
                if (text == now) return pick;
            }
            return null;
        }

        private void Save()
        {
            if (_path == null) return;
            try
            {
                var list = new List<KeyValuePair<string, List<StyleFinding>>>();
                foreach (var k in _order) list.Add(new KeyValuePair<string, List<StyleFinding>>(k, _items[k]));
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonConvert.SerializeObject(list), ProjectRules.NoBom);
            }
            catch (Exception ex) { Diagnostics.Log("review cache " + Diagnostics.Path(_path), ex); }
        }

        private static StyleFinding Copy(StyleFinding f)
        {
            return new StyleFinding
            {
                Source = f.Source, Rule = f.Rule, Severity = f.Severity, Excerpt = f.Excerpt, Suggestion = f.Suggestion,
                Replacement = f.Replacement, Layer = f.Layer, Link = f.Link, Variable = f.Variable, DocumentPath = f.DocumentPath, RuleId = f.RuleId, RunId = f.RunId
            };
        }
    }
}
