using System;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Logging;

namespace FlareAI.Flare
{
    /// <summary>
    /// Guards the plugin's own entry points (ribbon commands, menu items, button clicks), so an error never reaches Flare's
    /// or another vendor's process-wide exception handler. A bug gets "House Style ran into a problem" with Send (issue
    /// #124); an expected condition (a locked file, no network) gets the plain error message. No WinForms-wide handler is
    /// installed: Application.ThreadException can't pass other plugins' or Flare's errors on, so it would swallow them.
    /// </summary>
    internal static class Safe
    {
        public static void Run(string what, Action action)
        {
            try { CommandScope.Run(what, action); }
            catch (Exception ex)
            {
                try { Log.Current.Error("command.error", ex); } catch (Exception) { }
                try { Diagnostics.Log(what, ex); } catch (Exception) { }
                try { UI.Notify.Error(null, "House Style couldn't finish \"" + what + "\".", ex.Message, ex); }
                catch (Exception) { }
            }
        }

        /// <summary>The same action, guarded, for handing to Flare's command and menu APIs.</summary>
        public static Action Wrap(string what, Action action) { return () => Run(what, action); }
    }
}
