using System;
using System.ComponentModel;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Feedback;
using FlareAI.Core.Settings;
using FlareAI.Flare.UI;

namespace FlareAI.Flare
{
    /// <summary>
    /// Where House Style's bugs go (issue #124). A bug shows "House Style ran into a problem" straight away (or, with
    /// "Send error reports automatically" on, is sent in the background with a short notice). Expected conditions (see
    /// <see cref="ErrorKinds.IsExpected"/>: a locked or read-only file, no network, an AI service error) keep their plain
    /// message. Each error is offered once per session; a repeat shows the plain message only.
    /// </summary>
    internal static class ErrorReporter
    {
        public const string SentNotice = "House Style sent an error report.";

        /// <summary>
        /// A bug found where the writer didn't ask for anything (background work, editor events): reported only when the
        /// exception was thrown by House Style code, so Flare's own errors are left alone. Shown after the current event.
        /// </summary>
        public static void Background(Control owner, string whatFailed, Exception ex)
        {
            try
            {
                if (ex == null || !ErrorKinds.IsBug(ex) || !ErrorKinds.ThrownByUs(ex)) return;
                // A repeat of a background error says nothing: it would come back after every pause in typing.
                if (!SessionReports.Current.TryClaim(ex)) return;
                Action show = () => Show(owner, whatFailed, ex, null, null, true);
                if (owner != null && owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke(show);
                else show();
            }
            catch (Exception inner) { Diagnostics.Log("report background error", inner); }
        }

        /// <summary>The report dialog for a bug, or the background send when the writer chose that. Never throws.</summary>
        public static void Show(IWin32Window owner, string whatFailed, Exception ex, string action, string whatToDo = null, bool claimed = false)
        {
            try { FlareAI.Core.Logging.Log.Caught("error_report", ex); } catch (Exception) { }
            try
            {
                Diagnostics.Log("Error report offered: " + whatFailed, ex);
                if (!claimed && !SessionReports.Current.TryClaim(ex))
                {
                    // Offered once already this session: the plain message, so the writer still knows it failed.
                    Notify.PlainError(owner, whatFailed, whatToDo ?? "Try again.", ex, action);
                    return;
                }
                var ctx = FeedbackService.Context();
                if (ctx.Settings != null && ctx.Settings.SendErrorReportsAutomatically)
                {
                    SendInBackground(owner, ReportBuilder.ToJson(ReportBuilder.ForError(ctx, ex, whatFailed, null, true)));
                    return;
                }
                bool sent;
                using (var f = new ErrorReportForm(whatFailed, ex, ctx)) { f.ShowDialog(owner); sent = f.Result != null && f.Result.Ok; }
                if (sent) Toast.Show(FormOf(owner), ReportProblemForm.Sent);
            }
            catch (Exception inner)
            {
                Diagnostics.Log("error report", inner);
                try { Notify.PlainError(owner, whatFailed, whatToDo ?? "Try again.", ex, action); } catch (Exception) { }
            }
        }

        /// <summary>Automatic reports: sent in the background, then a short notice; when it failed, it is saved and the notice says where to send it.</summary>
        private static void SendInBackground(IWin32Window owner, string json)
        {
            var url = PluginSettings.Load().EffectiveFeedbackUrl;
            var form = FormOf(owner);
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) => e.Result = new FeedbackClient(url).Post("/v1/report", json);
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (form != null && !form.IsDisposed && form.InvokeRequired) { form.BeginInvoke((Action)(() => Notice(form, e, json))); return; }
                Notice(form, e, json);
            };
            worker.RunWorkerAsync();
        }

        private static void Notice(Form form, RunWorkerCompletedEventArgs e, string json)
        {
            var r = e.Error != null ? new FeedbackResult { Failure = FeedbackFailure.Network, Error = e.Error.Message } : (FeedbackResult)e.Result;
            Diagnostics.Log("Automatic error report: " + r);
            if (r.Ok) { Toast.Show(form, SentNotice); return; }
            // Saved at once, then the notice says where to send it: a new GitHub issue, or IT's address from settings.
            string path;
            try { path = ReportZip.Save(json, FeedbackForm.SaveFolder); }
            catch (Exception ex) { Diagnostics.Log("save automatic report", ex); Toast.Show(form, ReportZip.NotSaved); return; }
            var mail = ReportZip.MailtoLink(PluginSettings.Load().ReportEmail, path);
            Toast.Show(form, mail != null ? ReportZip.SavedForEmail : ReportZip.SavedForGitHub,
                       mail != null ? ReportZip.EmailLabel : ReportZip.OpenIssuesLabel, () => FeedbackForm.Launch(mail ?? ReportZip.IssuesUrl),
                       ReportZip.OpenFolderLabel, () => FeedbackForm.Launch(ReportZip.SelectInFolder(path)));
        }

        private static Form FormOf(IWin32Window owner)
        {
            var form = owner as Form;
            if (form == null && owner is Control) form = ((Control)owner).FindForm();
            return form ?? Form.ActiveForm;
        }
    }

    /// <summary>The install's details for reports: versions, Windows, settings, diag.log.</summary>
    internal static class FeedbackService
    {
        private static string _flareVersion;

        /// <summary>The plugin version stamped at build time, without "+commit".</summary>
        public static string PluginVersion { get { return ReportBuilder.VersionOf(typeof(FeedbackService).Assembly); } }

        /// <summary>Flare's product name and version, from the running program.</summary>
        public static string FlareVersion
        {
            get
            {
                if (_flareVersion != null) return _flareVersion;
                try
                {
                    var v = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileVersionInfo;
                    _flareVersion = ((v.ProductName ?? "") + " " + (v.ProductVersion ?? v.FileVersion ?? "")).Trim();
                }
                catch (Exception) { _flareVersion = "unknown"; }
                return _flareVersion;
            }
        }

        public static ReportContext Context() { return ReportContext.Current(PluginVersion, FlareVersion); }
    }
}
