using System;
using System.Linq;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Widths that follow the text at the current display scaling, so a filter or column reads in full at 200% as at 100%.
    /// A fixed pixel width doesn't grow with the font.
    /// </summary>
    internal static class Fit
    {
        /// <summary>The width of the longest choice, plus the drop-down arrow and the border.</summary>
        public static int ComboWidth(ComboBox box)
        {
            var text = box.Items.Cast<object>().Select(i => TextRenderer.MeasureText(box.GetItemText(i), box.Font).Width).DefaultIfEmpty(0).Max();
            return text + Arrow(box) + PaneRows.S(8, box.DeviceDpi / 96f);
        }

        /// <summary>True when every choice shows in full. The e2e hook reports the boxes that fail it.</summary>
        public static bool ComboFits(ComboBox box)
        {
            var text = box.Items.Cast<object>().Select(i => TextRenderer.MeasureText(box.GetItemText(i), box.Font).Width).DefaultIfEmpty(0).Max();
            return text + Arrow(box) <= box.Width;
        }

        /// <summary>Sizes the box to its choices now, and again when its font or scaling changes. Call it again after changing the choices.</summary>
        public static void Track(ComboBox box)
        {
            Size(box);
            box.FontChanged += (s, e) => Size(box);
            box.HandleCreated += (s, e) => Size(box);
            box.DpiChangedAfterParent += (s, e) => Size(box);
        }

        /// <summary>A box with no choices yet keeps its width, so it doesn't show as a sliver while they load.</summary>
        public static void Size(ComboBox box)
        {
            if (box.Items.Count == 0) return;
            box.Width = ComboWidth(box);
            box.DropDownWidth = box.Width;
        }

        /// <summary>The width a list view column needs for its header and its widest cell, with the list's cell padding.</summary>
        public static int ColumnWidth(ListView list, int column)
        {
            var widest = list.Items.Cast<ListViewItem>().Where(i => i.SubItems.Count > column).Select(i => i.SubItems[column].Text).Distinct()
                             .Concat(new[] { list.Columns[column].Text })
                             .Select(t => TextRenderer.MeasureText(t, list.Font).Width).DefaultIfEmpty(0).Max();
            return widest + PaneRows.S(16, list.DeviceDpi / 96f);
        }

        private static int Arrow(Control c) { return SystemInformation.GetVerticalScrollBarWidthForDpi(c.DeviceDpi); }
    }
}
