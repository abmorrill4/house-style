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
        public static List<StyleFinding> Listed(IEnumerable<StyleFinding> found, string anyPath, Dismissals dismissed)
        {
            var listed = new List<StyleFinding>();
            if (found == null) return listed;
            ExemptionList exemptions = null;
            try { exemptions = Exemptions.Load(anyPath); } catch (Exception ex) { Diagnostics.Log("toast exemptions", ex); }
            foreach (var f in found)
            {
                var path = f.DocumentPath ?? anyPath;
                if (dismissed != null && dismissed.IsDismissed(f, anyPath)) continue;
                var key = ReviewSession.ExemptRuleKey(f);
                var topic = key == null ? null : Exemptions.TopicPathFor(path);
                if (exemptions != null && topic != null && Exemptions.Covers(exemptions, topic, key, f.Excerpt, f.Before ?? "")) continue;
                listed.Add(f);
            }
            return listed;
        }
    }
}
