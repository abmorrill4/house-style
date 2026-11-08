using System;
using System.Collections.Generic;
using System.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// The issues a findings pane would list for a fresh check: what a toast counts. Leaves out the issues the writer
    /// ignored and the text the project exempts, as the pane does. Files the project excludes never reach a check.
    /// </summary>
    public static class ReportCount
    {
        /// <summary>
        /// True when the pane's results can be shown as they are: the pane lists as many issues as the notice said (or is still
        /// checking). The pane's count is made with <see cref="Listed"/> too, so it holds whether or not the pane keeps exempt rows.
        /// </summary>
        public static bool CanShowExisting(int paneListed, int noticeCount, bool paneBusy) { return paneBusy || paneListed == noticeCount; }

        /// <param name="marked">The pane's own findings: those it marked fixed (by hand) are not listed again, as the pane does.</param>
        public static List<StyleFinding> Listed(IEnumerable<StyleFinding> found, string anyPath, Dismissals dismissed, IEnumerable<StyleFinding> marked = null)
        {
            var listed = new List<StyleFinding>();
            if (found == null) return listed;
            ExemptionList exemptions = null;
            try { exemptions = Exemptions.Load(anyPath); } catch (Exception ex) { Diagnostics.Log("toast exemptions", ex); }
            var fixedByHand = new List<StyleFinding>();
            if (marked != null) foreach (var m in marked) if (m.MarkedFixed && !string.IsNullOrEmpty(m.Excerpt)) fixedByHand.Add(m);
            foreach (var f in found)
            {
                var path = f.DocumentPath ?? anyPath;
                if (dismissed != null && dismissed.IsDismissed(f, anyPath)) continue;
                var key = ReviewSession.ExemptRuleKey(f);
                var topic = key == null ? null : Exemptions.TopicPathFor(path);
                if (exemptions != null && topic != null && Exemptions.Covers(exemptions, topic, key, f.Excerpt, f.Before ?? "")) continue;
                if (!string.IsNullOrEmpty(f.Excerpt) && fixedByHand.Exists(m => m.RuleId == f.RuleId && m.DocumentPath == f.DocumentPath && HandFixes.ContextKey(m) == HandFixes.ContextKey(f))) continue;
                listed.Add(f);
            }
            return listed;
        }
    }
}
