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
