using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using FlareAI.Core.Style;
using Newtonsoft.Json;
using Formatting = Newtonsoft.Json.Formatting;

namespace FlareAI.Core.Actions
{
    /// <summary>What a writer asks the style guide owners to change.</summary>
    public static class RequestKinds
    {
        public const string TurnOff = "turn-off";
        public const string Severity = "severity";
        public const string Wording = "wording";
        public const string AddRule = "add-rule";
        public const string ProjectException = "project-exception";
        public const string TurnOn = "turn-on";

        /// <summary>"Turn off", "Change the level", … for lists and notices.</summary>
        public static string Label(string kind)
        {
            switch (kind)
            {
                case TurnOff: return "Disable";
                case Severity: return "Change the level";
                case Wording: return "Change the wording";
                case AddRule: return "Add a rule";
                case ProjectException: return "Project exception";
                case TurnOn: return "Enable";
                default: return kind ?? "";
            }
        }
    }

    /// <summary>A request's state: <c>open</c> until an owner approves or declines it, or the writer withdraws it.</summary>
    public static class RequestStatus
    {
        public const string Open = "open";
        public const string Approved = "approved";
        public const string Declined = "declined";
        public const string Withdrawn = "withdrawn";
    }

    /// <summary>
    /// One writer's request to the style guide owners, with a reason. Saved as one small file in the requests folder (by
    /// default the team feedback folder's <c>requests</c> subfolder), never edited after it is written: decisions are
    /// separate files, so no two people ever write the same file.
    /// </summary>
    public sealed class GuideRequest
    {
        public string Id { get; set; }
        /// <summary>Who asked (the feedback identity: an anonymous id unless the guide's attribution is names).</summary>
        public string Writer { get; set; }
        public DateTime Created { get; set; }
        /// <summary>One of <see cref="RequestKinds"/>.</summary>
        public string Kind { get; set; }
        public string RuleId { get; set; }
        /// <summary>The rule as the writer saw it (its linter or AI name).</summary>
        public string Rule { get; set; }
        public string Layer { get; set; }
        /// <summary>The rule's wording when asked (to find it again for a wording change).</summary>
        public string RuleText { get; set; }
        /// <summary>The guide topic the rule is written in, when known.</summary>
        public string RuleFile { get; set; }
        /// <summary>Proposed level: off, suggestion, warning or error.</summary>
        public string Setting { get; set; }
        /// <summary>Proposed wording (a wording change).</summary>
        public string Wording { get; set; }
        /// <summary>The rule to add (an add-rule request).</summary>
        public RuleCandidate Candidate { get; set; }
        /// <summary>Project name (a project exception).</summary>
        public string Project { get; set; }
        /// <summary>A path in the writer's project, on the writer's machine: where the approved exception is written.</summary>
        public string ProjectPath { get; set; }
        public string Reason { get; set; }
        /// <summary>Example sentences and files the writer saw.</summary>
        public List<string> Evidence { get; set; } = new List<string>();

        // ---- read from the decision file, not stored with the request ----
        [JsonIgnore] public string Status { get; set; } = RequestStatus.Open;
        [JsonIgnore] public RequestDecision Decision { get; set; }
        /// <summary>Read from an older "propose-ignore" feedback line (before requests were files).</summary>
        [JsonIgnore] public bool IsLegacy { get; set; }
        /// <summary>Waiting in the writer's outbox: the requests folder couldn't be reached when it was sent.</summary>
        [JsonIgnore] public bool Unsent { get; set; }

        [JsonIgnore] public bool IsOpen { get { return Status == RequestStatus.Open; } }

        /// <summary>"Turn off: Google.WordList (to Off)".</summary>
        [JsonIgnore]
        public string Title { get { return TitleWith(null); } }

        /// <summary>The title with the rule shown by <paramref name="nameOf"/> (a readable name) instead of its internal id; null keeps the id.</summary>
        public string TitleWith(Func<GuideRequest, string> nameOf)
        {
            {
                var named = nameOf == null ? null : nameOf(this);
                var what = Kind == RequestKinds.AddRule && Candidate != null ? Candidate.Title : !string.IsNullOrEmpty(named) ? named : (Rule ?? RuleId ?? "");
                var to = Kind == RequestKinds.Severity || Kind == RequestKinds.ProjectException ? " (to " + FlareAI.Core.Severity.Label(Setting ?? "off") + ")"
                       : Kind == RequestKinds.Wording ? ": \"" + ReviewSession.Shorten(Wording, 60) + "\"" : "";
                return RequestKinds.Label(Kind) + ": " + what + to + (Kind == RequestKinds.ProjectException && !string.IsNullOrEmpty(Project) ? " in " + Project : "");
            }
        }

        /// <summary>"Open", "Approved by lead on 2026-10-01", "Declined: note", "Not sent yet".</summary>
        [JsonIgnore]
        public string Outcome
        {
            get
            {
                if (Unsent) return "Not sent yet. House Style couldn't reach the requests folder and tries again the next time you lint.";
                var d = Decision;
                switch (Status)
                {
                    case RequestStatus.Approved: return "Approved" + By(d) + (d != null && !string.IsNullOrEmpty(d.Note) ? ": " + d.Note : "");
                    case RequestStatus.Declined: return "Declined" + By(d) + (d != null && !string.IsNullOrEmpty(d.Note) ? ": " + d.Note : "");
                    case RequestStatus.Withdrawn: return "Withdrawn";
                    default: return "Open: waiting for an owner";
                }
            }
        }

        private static string By(RequestDecision d) { return d == null ? "" : " by " + d.By + " on " + d.At.ToLocalTime().ToString("yyyy-MM-dd"); }
    }

    /// <summary>An owner's decision (or the writer's withdrawal): its own file next to the request, created once. The first wins.</summary>
    public sealed class RequestDecision
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string By { get; set; }
        public DateTime At { get; set; }
        public string Note { get; set; }
        /// <summary>The change as approved (the owner may edit the proposal first): level, wording, rule.</summary>
        public string Setting { get; set; }
        public string Wording { get; set; }
        public RuleCandidate Candidate { get; set; }
    }

    /// <summary>What the writer's next check found: outcomes to tell them about, and the rules they're still waiting on.</summary>
    public sealed class RequestCheck
    {
        public List<string> Notices = new List<string>();
        /// <summary>Rule ids (or names) with an open request from this writer: their findings show "Request sent".</summary>
        public HashSet<string> OpenRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>One line for the status bar, or null.</summary>
        public string Notice { get { return Notices.Count == 0 ? null : string.Join("  ", Notices.ToArray()); } }
    }

    /// <summary>
    /// The request lifecycle: a writer sends (one file each), owners approve or decline in Tune style guide (one decision
    /// file each, first wins), the writer withdraws an open one, and the writer's next check shows the outcome and writes an
    /// approved project exception into their own project. What this machine sent, saw and applied is kept locally
    /// (<c>%APPDATA%\FlareAI\requests.json</c>), and requests that couldn't be sent wait in an outbox, so nothing is lost.
    /// </summary>
    public static class GuideRequests
    {
        private static readonly object Gate = new object();
        private const string DecisionSuffix = ".decision.json";

        /// <summary>The shared requests folder (the team feedback folder's <c>requests</c>), or null without one.</summary>
        public static string Folder(Team team)
        {
            return team == null || string.IsNullOrEmpty(team.FeedbackFolder) ? null : Path.Combine(team.FeedbackFolder, "requests");
        }

        private static string Outbox { get { return Paths.In("requests-outbox"); } }
        private static string LedgerPath { get { return Paths.In("requests.json"); } }

        /// <summary>A new request from this writer, with an id and the date filled in.</summary>
        public static GuideRequest New(Team team, string kind, string reason)
        {
            var now = DateTime.UtcNow;
            return new GuideRequest
            {
                Id = "r-" + now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Writer = team == null ? Team.AnonymousId() : team.FeedbackWriter, Created = now, Kind = kind, Reason = (reason ?? "").Trim()
            };
        }

        /// <summary>
        /// Saves the request in the requests folder. When that can't be written (no folder set, the share is offline), the
        /// request waits in this writer's outbox and is sent on a later check; <paramref name="problem"/> says why.
        /// Returns true when it reached the shared folder.
        /// </summary>
        public static bool Send(Team team, GuideRequest r, out string problem)
        {
            if (r == null) throw new ArgumentNullException("r");
            if (string.IsNullOrEmpty(r.Reason)) throw new ArgumentException("A request needs a reason.");
            problem = null;
            if (!IsValidId(r.Id)) throw new ArgumentException("A request id is letters, digits, dots, dashes and underscores.");
            var json = JsonConvert.SerializeObject(r, Formatting.Indented);
            // The local copy is what this machine applies later: the shared file can be rewritten by anyone with the folder.
            Remember(l => { l.Mine.Add(r.Id); l.Sent[r.Id] = JsonConvert.DeserializeObject<GuideRequest>(json); });
            var folder = Folder(team);
            if (folder == null) problem = "no team feedback folder is set in Settings, so the owners can't receive requests";
            else
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    WriteNew(Path.Combine(folder, r.Id + ".json"), json);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Diagnostics.Log("request " + folder, ex);
                    problem = "House Style couldn't write to " + folder + " (" + ex.Message.TrimEnd('.', ' ') + ")";
                }
            }
            try
            {
                Directory.CreateDirectory(Outbox);
                File.WriteAllText(Path.Combine(Outbox, r.Id + ".json"), json, ProjectRules.NoBom);
                r.Unsent = true;
            }
            catch (Exception ex) { Diagnostics.Log("request outbox", ex); }
            return false;
        }

        /// <summary>
        /// Every request the team can see: the files in the requests folder, older "propose-ignore" feedback lines (read as
        /// turn-off requests, nothing lost), and this writer's unsent ones. Each carries its decision, if any. Newest first.
        /// </summary>
        public static List<GuideRequest> Load(Team team)
        {
            var list = new List<GuideRequest>();
            var folder = Folder(team);
            var decisions = new Dictionary<string, RequestDecision>(StringComparer.OrdinalIgnoreCase);
            if (folder != null && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        var text = ReadShared(file);
                        var name = Path.GetFileName(file);
                        // Anyone with the folder can drop a file here: only well-formed ids, each in the file named after it.
                        if (file.EndsWith(DecisionSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            var d = JsonConvert.DeserializeObject<RequestDecision>(text);
                            if (d != null && IsValidId(d.Id) && string.Equals(name, d.Id + DecisionSuffix, StringComparison.OrdinalIgnoreCase)) decisions[d.Id] = d;
                        }
                        else
                        {
                            var r = JsonConvert.DeserializeObject<GuideRequest>(text);
                            if (r != null && IsValidId(r.Id) && string.Equals(name, r.Id + ".json", StringComparison.OrdinalIgnoreCase)) list.Add(r);
                        }
                    }
                    catch (Exception ex) { Diagnostics.Log("request " + file, ex); }   // a file still syncing: read it next time
                }
            }
            if (team != null) list.AddRange(Legacy(team.FeedbackFolder));
            if (Directory.Exists(Outbox))
                foreach (var file in Directory.GetFiles(Outbox, "*.json"))
                {
                    try
                    {
                        var r = JsonConvert.DeserializeObject<GuideRequest>(File.ReadAllText(file, Encoding.UTF8));
                        if (r != null && IsValidId(r.Id) && !list.Exists(x => x.Id == r.Id)) { r.Unsent = true; list.Add(r); }
                    }
                    catch (Exception ex) { Diagnostics.Log("request outbox " + file, ex); }
                }
            foreach (var r in list)
            {
                RequestDecision d;
                if (decisions.TryGetValue(r.Id, out d)) { r.Decision = d; r.Status = d.Status ?? RequestStatus.Open; }
            }
            return list.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                       .OrderByDescending(r => r.IsOpen).ThenByDescending(r => r.Created).ToList();
        }

        /// <summary>True when this machine sent the request (or an older proposal carries this writer's id).</summary>
        public static bool IsMine(Team team, GuideRequest r) { return IsMine(team, r, ReadLedger()); }

        private static bool IsMine(Team team, GuideRequest r, Ledger ledger)
        {
            if (r == null) return false;
            if (ledger.Mine.Contains(r.Id)) return true;
            return team != null && r.Writer != null &&
                   (string.Equals(r.Writer, team.FeedbackWriter, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Writer, team.Writer, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// An owner approves the request: claims it (the decision file is created once; if another owner got there first,
        /// returns their decision and changes nothing), then applies the change through <paramref name="apply"/>. If applying
        /// fails, the claim is removed so the request stays open, and the exception propagates. Returns null when done.
        /// </summary>
        public static RequestDecision Approve(Team team, GuideRequest r, RequestDecision edits, Action<GuideRequest, RequestDecision> apply)
        {
            var d = edits ?? new RequestDecision();
            d.Setting = d.Setting ?? r.Setting; d.Wording = d.Wording ?? r.Wording; d.Candidate = d.Candidate ?? r.Candidate;
            return Decide(team, r, RequestStatus.Approved, d, apply);
        }

        /// <summary>An owner declines, optionally with a note. Returns the earlier decision when someone already decided.</summary>
        public static RequestDecision Decline(Team team, GuideRequest r, string note)
        {
            return Decide(team, r, RequestStatus.Declined, new RequestDecision { Note = note }, null);
        }

        /// <summary>The writer withdraws an open request of theirs. Returns the earlier decision when an owner already decided.</summary>
        public static RequestDecision Withdraw(Team team, GuideRequest r)
        {
            if (!IsMine(team, r)) throw new UnauthorizedAccessException("Only the writer who sent a request can withdraw it.");
            if (r.Unsent)
            {
                try { File.Delete(Path.Combine(Outbox, r.Id + ".json")); } catch (Exception ex) { Diagnostics.Log("withdraw outbox", ex); }
                r.Status = RequestStatus.Withdrawn;
                return null;
            }
            return Decide(team, r, RequestStatus.Withdrawn, new RequestDecision(), null);
        }

        private static RequestDecision Decide(Team team, GuideRequest r, string status, RequestDecision d, Action<GuideRequest, RequestDecision> apply)
        {
            if (r == null) throw new ArgumentNullException("r");
            if (!IsValidId(r.Id)) throw new ArgumentException("This request's id isn't valid, so it can't be decided.");
            if (status != RequestStatus.Withdrawn && team != null && team.IsStrict && !team.IsListedOwner)
                throw new UnauthorizedAccessException("Only the style guide owners (" + team.OwnerNames + ") can decide requests.");
            var folder = Folder(team);
            if (folder == null) throw new InvalidOperationException("No team feedback folder is set in Settings, so there are no shared requests to decide.");
            d.Id = r.Id; d.Status = status; d.At = DateTime.UtcNow;
            d.By = team == null ? Environment.UserName : status == RequestStatus.Withdrawn ? r.Writer : team.Writer;
            var path = Path.Combine(folder, r.Id + DecisionSuffix);
            var earlier = ReadDecision(path);
            if (earlier != null) { r.Decision = earlier; r.Status = earlier.Status; return earlier; }
            try
            {
                Directory.CreateDirectory(folder);
                WriteNew(path, JsonConvert.SerializeObject(d, Formatting.Indented));
            }
            catch (IOException) when (File.Exists(path))
            {
                earlier = ReadDecision(path);   // another owner decided a moment ago: theirs stands
                if (earlier != null) { r.Decision = earlier; r.Status = earlier.Status; return earlier; }
                throw;
            }
            if (apply != null)
            {
                try { apply(r, d); }
                catch
                {
                    try { File.Delete(path); } catch (Exception ex) { Diagnostics.Log("request: undo claim", ex); }
                    throw;
                }
            }
            r.Decision = d; r.Status = status;
            return null;
        }

        /// <summary>
        /// The writer's check: sends anything waiting in the outbox, reports outcomes they haven't seen ("Approved", "Declined:
        /// note"), writes approved project exceptions into their project (once; a project that isn't on this machine waits),
        /// and lists the rules still waiting on an answer.
        /// </summary>
        public static RequestCheck Check(Team team)
        {
            var result = new RequestCheck();
            if (team == null || !team.IsStrict) return result;
            Flush(team);
            var ledger = ReadLedger();
            var changed = false;
            foreach (var r in Load(team))
            {
                if (!IsMine(team, r, ledger)) continue;
                if (r.IsOpen) { result.OpenRules.Add(r.RuleId ?? r.Rule ?? ""); continue; }
                if (r.Status == RequestStatus.Withdrawn) continue;
                if (!ledger.Seen.Contains(r.Id))
                {
                    result.Notices.Add("Style guide request " + (r.Status == RequestStatus.Approved ? "approved" : "declined") + ": " + r.Title + ". " + r.Outcome + ".");
                    ledger.Seen.Add(r.Id); changed = true;
                }
                // Only a request this machine sent, as it was sent: the shared folder proves nothing about who wrote a file in it.
                if (r.Status == RequestStatus.Approved && r.Kind == RequestKinds.ProjectException && !ledger.Applied.Contains(r.Id) && ledger.Mine.Contains(r.Id))
                {
                    GuideRequest sent;
                    var local = ledger.Sent.TryGetValue(r.Id, out sent) && sent != null && sent.Kind == RequestKinds.ProjectException ? sent : null;
                    // Sent before local copies were kept: the shared copy, but never a network path.
                    if (local == null && IsNetworkPath(r.ProjectPath)) continue;
                    var apply = local ?? r;
                    try
                    {
                        if (ApplyProjectException(apply, r.Decision))
                        {
                            ledger.Applied.Add(r.Id); changed = true;
                            result.Notices.Add("The approved exception is now in " + ProjectRules.OverridesPath(apply.ProjectPath) + ".");
                        }
                    }
                    catch (Exception ex) { Diagnostics.Log("apply approved exception " + r.Id, ex); }
                }
            }
            if (changed) SaveLedger(ledger);
            return result;
        }

        /// <summary>
        /// Writes an approved project exception into the project's overrides.ini, with "# by OWNER on DATE: reason [approved
        /// request id]" above it. False when the project isn't on this machine (it is tried again on a later check).
        /// </summary>
        public static bool ApplyProjectException(GuideRequest r, RequestDecision d)
        {
            if (string.IsNullOrEmpty(r.ProjectPath) || ProjectRules.OverridesPath(r.ProjectPath) == null) return false;
            var note = r.Reason + (d != null && !string.IsNullOrEmpty(d.Note) ? " Owner: " + d.Note : "");
            var setting = (d != null ? d.Setting : null) ?? r.Setting ?? "off";
            if (!Settings.Contains(setting)) throw new InvalidOperationException("Unknown level in an approved exception: " + setting);
            ProjectRules.Ignore(r.ProjectPath, new StyleFinding { RuleId = r.RuleId, Rule = r.Rule, Layer = r.Layer }, setting,
                                RuleOverrides.WithRequest(note, r.Id), d != null ? d.By : null);
            return true;
        }

        private static readonly HashSet<string> Settings = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "off", "suggestion", "warning", "error" };
        private static readonly System.Text.RegularExpressions.Regex IdPattern = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

        /// <summary>A request id that is safe as a file name: letters, digits, dots, dashes and underscores (what <see cref="New"/> makes).</summary>
        public static bool IsValidId(string id) { return id != null && IdPattern.IsMatch(id) && !id.Contains(".."); }

        /// <summary>A UNC path (<c>\\host\share</c>): looking at it makes Windows sign in to that host.</summary>
        private static bool IsNetworkPath(string path)
        {
            return !string.IsNullOrEmpty(path) && (path.StartsWith(@"\\") || path.StartsWith("//") || path.StartsWith(@"\/") || path.StartsWith(@"/\"));
        }

        /// <summary>
        /// Applies an approved change to the team guide (owners): a level or turn-off row, turning a rule back on, a wording
        /// change in the rule's topic, or a new rule. A project exception changes nothing in the guide (the writer's plugin
        /// writes it into their project). Throws <see cref="GuideWriteException"/> naming the file when it can't be written.
        /// </summary>
        public static void ApplyToGuide(Team team, GuideRequest r, RequestDecision d)
        {
            var reason = "Request " + r.Id + ": " + r.Reason + (string.IsNullOrEmpty(d.Note) ? "" : " Owner: " + d.Note);
            // Every kind but a project exception writes into the team's guide: never into a folder that isn't there.
            if (r.Kind != RequestKinds.ProjectException) GuideOverrides.RefuseUnreachable(team);
            switch (r.Kind)
            {
                case RequestKinds.TurnOff:
                case RequestKinds.Severity:
                    GuideOverrides.Set(team, r.RuleId, d.Setting ?? (r.Kind == RequestKinds.TurnOff ? "off" : "warning"), reason);
                    break;
                case RequestKinds.TurnOn:
                    team.EnsureOwner();
                    var rows = RuleOverrides.Read(team.WritableGuide(), "Team style guide").Where(o => string.Equals(o.RuleId, r.RuleId, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var o in rows) RuleOverrides.TurnBackOn(o, team);
                    // No row of its own: the rule is off by a default of the bundled guide, which only an "on" row overrides.
                    if (rows.Count == 0 && !string.Equals(r.Setting, "on", StringComparison.OrdinalIgnoreCase))
                        GuideOverrides.Set(team, r.RuleId, "on", reason);
                    break;
                case RequestKinds.Wording:
                    team.EnsureOwner();
                    // Only the team guide's own topics: never the generated Google rules or a guide elsewhere (the plugin folder).
                    var root = Path.GetFullPath(team.WritableGuide()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var file = string.IsNullOrEmpty(r.RuleFile) ? null : Path.GetFullPath(r.RuleFile);
                    if (file == null || !file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        file.Substring(root.Length).Split('\\', '/').Any(part => part.Equals("Google", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("This rule isn't written in the team's style guide (Google rules are generated), so its wording can't be changed here. Change its level instead.");
                    Reword(file, r.RuleText, d.Wording ?? r.Wording);
                    break;
                case RequestKinds.AddRule:
                    team.EnsureOwner();
                    var candidate = d.Candidate ?? r.Candidate;
                    if (candidate == null) throw new InvalidOperationException("This request has no rule to add.");
                    GuideBuilder.Write(Path.Combine(GuideOverrides.StyleFolder(team.WritableGuide()), "From-our-content.htm"), new List<RuleCandidate> { candidate }, "From our content");
                    break;
                case RequestKinds.ProjectException:
                    break;
                default:
                    throw new InvalidOperationException("Unknown request kind: " + r.Kind);
            }
        }

        /// <summary>Replaces a prose rule's wording (its bullet's own text, or its paragraph) in the guide topic.</summary>
        public static void Reword(string file, string ruleText, string wording)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) throw new InvalidOperationException("House Style doesn't know which guide topic holds this rule. Open the rule in the guide and change its wording there.");
            if (string.IsNullOrEmpty(wording)) throw new ArgumentException("The new wording is empty.");
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(file);
            var block = GuideEdits.Locate(doc, ruleText);
            if (block == null) throw new InvalidOperationException("House Style couldn't find this rule in " + file + ". It may have changed since the request; change its wording there.");
            if (block.LocalName == "tr") throw new InvalidOperationException("This rule is a table row in " + file + ": change its cells there (Open rule in the guide).");
            var keep = block.ChildNodes.Cast<XmlNode>().Where(c => c.NodeType == XmlNodeType.Element && (c.LocalName == "ul" || c.LocalName == "ol")).ToList();
            block.InnerXml = "";
            block.AppendChild(doc.CreateTextNode(wording.Trim()));
            foreach (var k in keep) block.AppendChild(k);
            try { GuideLayer.Save(doc, file); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
        }

        // ---- storage --------------------------------------------------------------------------------

        /// <summary>Older proposals: "propose-ignore" lines in the feedback files, as open turn-off requests with a stable id.</summary>
        private static IEnumerable<GuideRequest> Legacy(string feedbackFolder)
        {
            foreach (var d in FeedbackStats.Load(FeedbackStats.FilesIn(feedbackFolder)).Where(x => x.Verdict == "propose-ignore"))
            {
                yield return new GuideRequest
                {
                    Id = "legacy-" + FeedbackLog.WordingHash(d.Writer + "|" + d.At.ToString("o") + "|" + d.RuleId + "|" + d.Rule + "|" + d.Reason) + FeedbackLog.WordingHash(d.Excerpt + "|" + d.File),
                    Writer = d.Writer, Created = d.At, Kind = RequestKinds.TurnOff, RuleId = d.RuleId, Rule = d.Rule ?? d.RuleId, Layer = d.Layer, Setting = "off",
                    Reason = d.Reason ?? "", IsLegacy = true,
                    Evidence = string.IsNullOrEmpty(d.Excerpt) ? new List<string>() : new List<string> { "\"" + d.Excerpt + "\" (" + Path.GetFileName(d.File ?? "") + ")" }
                };
            }
        }

        /// <summary>Sends requests waiting in the outbox (each once; a copy already in the folder counts as sent).</summary>
        private static void Flush(Team team)
        {
            var folder = Folder(team);
            if (folder == null || !Directory.Exists(Outbox)) return;
            foreach (var file in Directory.GetFiles(Outbox, "*.json"))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var target = Path.Combine(folder, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                    File.Delete(file);
                }
                catch (Exception ex) { Diagnostics.Log("request outbox: send " + file, ex); return; }   // still offline: try next time
            }
        }

        private static RequestDecision ReadDecision(string path)
        {
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<RequestDecision>(ReadShared(path)) : null; }
            catch (Exception ex) { Diagnostics.Log("decision " + path, ex); return null; }
        }

        /// <summary>Creates the file; throws IOException when it already exists (that is how the first decision wins).</summary>
        private static void WriteNew(string path, string text)
        {
            var bytes = ProjectRules.NoBom.GetBytes(text);
            using (var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) s.Write(bytes, 0, bytes.Length);
        }

        private static string ReadShared(string file)
        {
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        private sealed class Ledger
        {
            public HashSet<string> Mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Each request as this machine sent it, by id.</summary>
            public Dictionary<string, GuideRequest> Sent = new Dictionary<string, GuideRequest>(StringComparer.OrdinalIgnoreCase);
        }

        private static Ledger ReadLedger()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(LedgerPath))
                    {
                        var l = JsonConvert.DeserializeObject<Ledger>(File.ReadAllText(LedgerPath, Encoding.UTF8));
                        if (l != null)
                        {
                            l.Mine = new HashSet<string>(l.Mine ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                            l.Seen = new HashSet<string>(l.Seen ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                            l.Applied = new HashSet<string>(l.Applied ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                            l.Sent = new Dictionary<string, GuideRequest>(l.Sent ?? new Dictionary<string, GuideRequest>(), StringComparer.OrdinalIgnoreCase);
                            return l;
                        }
                    }
                }
                catch (Exception ex) { Diagnostics.Log("requests.json", ex); }
                return new Ledger();
            }
        }

        private static void SaveLedger(Ledger l)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath));
                    File.WriteAllText(LedgerPath, JsonConvert.SerializeObject(l, Formatting.Indented), ProjectRules.NoBom);
                }
                catch (Exception ex) { Diagnostics.Log("requests.json", ex); }
            }
        }

        private static void Remember(Action<Ledger> change)
        {
            lock (Gate)
            {
                var l = ReadLedger();
                change(l);
                SaveLedger(l);
            }
        }
    }
}
