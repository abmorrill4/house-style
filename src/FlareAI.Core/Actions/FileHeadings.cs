namespace FlareAI.Core.Actions
{
    /// <summary>The text of a file heading in the By file view.</summary>
    public static class FileHeadings
    {
        /// <summary>"▾ billpay\faq.htm · 3 issues" (expanded) or "▸ ..." (collapsed).</summary>
        public static string Text(string name, int count, bool collapsed)
        {
            return (collapsed ? "▸ " : "▾ ") + name + " · " + Plural.Of(count, "issue");
        }
    }
}
