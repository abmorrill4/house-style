using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using FlareAI.Flare;
using Xunit;

namespace FlareAI.Flare.Tests
{
    public class E2EHookErrorTests
    {
        [Fact]
        public void Valid_ListsNames_CapsTheList_AndSaysNone()
        {
            Assert.Equal(" (valid: a, b)", E2EHook.Valid(new[] { "a", "b" }));
            Assert.Equal(" (none)", E2EHook.Valid(new string[0]));
            var many = E2EHook.Valid(Enumerable.Range(1, 31).Select(i => "n" + i));
            Assert.EndsWith("n30, ...)", many);
            Assert.DoesNotContain("n31", many);
        }

        private sealed class LockedPanel : Panel
        {
            public bool Locked;
            protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
            {
                if (Locked) { width = Width; height = Height; }
                base.SetBoundsCore(x, y, width, height, specified);
            }
        }

        private static int Read(object size, string name)
        {
            return (int)size.GetType().GetProperty(name).GetValue(size, null);
        }

        [Fact]
        public void SizePane_Docked_AcceptsItsCurrentSize_ResizesOtherwise_AndFailsWhenKept()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false })
            {
                var panel = new LockedPanel { Size = new Size(400, 600) };
                var pane = new UserControl { Dock = DockStyle.Fill };
                panel.Controls.Add(pane);
                form.Controls.Add(panel);
                form.Show();

                // (a) already the requested size: returns it without failing
                var same = pane.ClientSize;
                var result = E2EHook.SizePane(pane, same.Width, same.Height);
                Assert.Equal(same.Width, Read(result, "width"));
                Assert.Equal(same.Height, Read(result, "height"));
                Assert.Equal(same, pane.ClientSize);

                // (b) a different size resizes the holder
                pane.Dock = DockStyle.None;
                pane.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                var other = new Size(same.Width - 50, same.Height - 40);
                var resized = E2EHook.SizePane(pane, other.Width, other.Height);
                Assert.Equal(other, pane.ClientSize);
                Assert.Equal(other.Width, Read(resized, "width"));

                // (c) the holder keeps its size: still fails
                pane.Dock = DockStyle.Fill;
                panel.Locked = true;
                var ex = Assert.Throws<InvalidOperationException>(() => E2EHook.SizePane(pane, pane.ClientSize.Width + 30, pane.ClientSize.Height + 30));
                Assert.Equal("Flare didn't resize the docked pane.", ex.Message);
            }
        }

        [Fact]
        public void Helpers_NameTheValidChoices_WhenNothingMatches()
        {
            using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false })
            {
                form.Controls.Add(new Button { Text = "&Change" });
                form.Controls.Add(new Button { Text = "Cancel" });
                form.Controls.Add(new TextBox { AccessibleName = "Name" });
                form.Controls.Add(new TextBox { AccessibleName = "Key", UseSystemPasswordChar = true });
                var tabs = new TabControl();
                tabs.TabPages.Add(new TabPage("&Rules"));
                form.Controls.Add(tabs);
                form.Show();

                var click = Assert.Throws<InvalidOperationException>(() => E2EHook.Click(form, "Nope")).Message;
                Assert.StartsWith("No enabled button Nope", click);
                Assert.Contains("Change", click);
                Assert.Contains("Cancel", click);
                var type = Assert.Throws<InvalidOperationException>(() => E2EHook.Type(form, "Nope", "x")).Message;
                Assert.Contains("Name", type);
                Assert.DoesNotContain("Key", type);
                Assert.Contains("Rules", Assert.Throws<InvalidOperationException>(() => E2EHook.Tab(form, "Nope")).Message);
            }
        }
    }
}
