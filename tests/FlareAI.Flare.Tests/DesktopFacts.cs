using System;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>
    /// A test that needs an interactive desktop (window focus, a MessageBox). It is skipped under a scheduled task or a
    /// service, where Windows gives the process no desktop and focus never arrives. The build factory's test runs are
    /// one such place; CI's hosted runners and a signed-in developer have a desktop and run it.
    /// </summary>
    public sealed class DesktopFactAttribute : FactAttribute
    {
        public DesktopFactAttribute() { if (!Environment.UserInteractive) Skip = "Needs an interactive desktop."; }
    }

    /// <summary>The <see cref="TheoryAttribute"/> form of <see cref="DesktopFactAttribute"/>.</summary>
    public sealed class DesktopTheoryAttribute : TheoryAttribute
    {
        public DesktopTheoryAttribute() { if (!Environment.UserInteractive) Skip = "Needs an interactive desktop."; }
    }
}
