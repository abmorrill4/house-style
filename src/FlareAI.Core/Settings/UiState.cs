using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace FlareAI.Core.Settings
{
    /// <summary>Where a window was last (size, position, maximised) and its list's column widths.</summary>
    public sealed class WindowState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
        public List<int> Columns { get; set; }

        /// <summary>
        /// Column widths the writer dragged, in logical (96 DPI) pixels so they scale with the display; 0 for a column left to
        /// fit its text. Replaces <see cref="Columns"/>, whose physical pixels also held a list's own default widths.
        /// </summary>
        public List<int> LogicalColumns { get; set; }
    }

    /// <summary>
    /// A small per-user file (ui-state.json under <see cref="Paths.Root"/>) remembering each window's size, position,
    /// maximised state and column widths. Never throws: a missing or damaged file means "no memory".
    /// </summary>
    public static class UiState
    {
        public static string FilePath { get { return Paths.In("ui-state.json"); } }

        /// <summary>The remembered state of window <paramref name="name"/>, or null.</summary>
        public static WindowState Load(string name)
        {
            try
            {
                var all = ReadAll();
                WindowState s;
                return all.TryGetValue(name, out s) ? s : null;
            }
            catch (Exception ex) { Diagnostics.Log("ui state: read", ex); return null; }
        }

        /// <summary>Remembers <paramref name="state"/> for window <paramref name="name"/>.</summary>
        public static void Save(string name, WindowState state)
        {
            try
            {
                var all = ReadAll();
                all[name] = state;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(all, Formatting.Indented));
            }
            catch (Exception ex) { Diagnostics.Log("ui state: write", ex); }
        }

        private static Dictionary<string, WindowState> ReadAll()
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, WindowState>();
            try { return JsonConvert.DeserializeObject<Dictionary<string, WindowState>>(File.ReadAllText(FilePath)) ?? new Dictionary<string, WindowState>(); }
            catch (JsonException) { return new Dictionary<string, WindowState>(); }
        }

        /// <summary>
        /// The size to open at: the remembered one when it fits the screen's working area, else about 80% of
        /// <paramref name="ownerW"/> × <paramref name="ownerH"/> (Flare's main window), at least 900 × 600, never larger than the screen.
        /// </summary>
        public static void Fit(WindowState saved, int ownerW, int ownerH, int screenW, int screenH, out int width, out int height, int minW = 900, int minH = 600)
        {
            width = saved != null && saved.Width > 0 ? saved.Width : Math.Max(minW, ownerW * 8 / 10);
            height = saved != null && saved.Height > 0 ? saved.Height : Math.Max(minH, ownerH * 8 / 10);
            width = Math.Min(width, screenW);
            height = Math.Min(height, screenH);
        }

        /// <summary>True when a window at this place is at least partly on a screen (<paramref name="screens"/>: x, y, width, height).</summary>
        public static bool IsVisible(WindowState s, IEnumerable<int[]> screens)
        {
            if (s == null) return false;
            foreach (var r in screens)
                if (s.X + s.Width > r[0] + 40 && s.X < r[0] + r[2] - 40 && s.Y >= r[1] - 10 && s.Y < r[1] + r[3] - 40) return true;
            return false;
        }
    }
}
