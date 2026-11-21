// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Bimbridge.Engine;
using Bimbridge.Protocol;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;
using Typography = System.Windows.Documents.Typography;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// "BIMBRIDGE - Power Engine" control center (modeless): summary, named agents, attributed activity, safety and recovery,
    /// performance and export. It only reads in-memory state and the queue on disk; it never calls the Revit API or holds Document/Element,
    /// so it can stay open while you work. It redraws at most every 1.5 s and only if something changed.
    /// </summary>
    internal sealed class StatusWindow
    {
        private static StatusWindow _open;
        private static DateTime StartedUtc => Runtime.StartedUtc;
        private readonly Window _win;
        private readonly TextBlock _subtitle, _pillText;
        private readonly Border _pill;
        // _hero = at-a-glance answer; the rest lives in collapsible sections (progressive disclosure).
        private readonly StackPanel _hero = new StackPanel(), _system = new StackPanel(), _agents = new StackPanel(), _activity = new StackPanel(),
            _safety = new StackPanel(), _perf = new StackPanel(), _engine = new StackPanel(), _opsHead = new StackPanel(), _opsList = new StackPanel();
        private readonly Fold _agentsFold, _opsFold, _safetyFold, _activityFold, _systemFold;
        private const string CancelTag = "status-cancel:";
        // State of each section (by title) during the Revit session: survives closing and reopening the window.
        private static readonly Dictionary<string, bool> Expanded = new Dictionary<string, bool>();
        private static readonly Dictionary<string, bool> Attention = new Dictionary<string, bool>();

        /// <summary>Collapsible section created once: its ToggleButton remembers what the person chose during the Revit session.</summary>
        private sealed class Fold
        {
            public string Title;
            public ToggleButton Toggle;
            public Border Chip;
            public bool LastAttention;
        }
        /// <summary>#5 (0.9.1): filter for the long-operation history (null = all).</summary>
        private string _opsFilter;
        private static readonly (string Label, string State)[] OpsFilters =
        {
            ("All", null), ("With differences", Status.CompletedWithDifferences), ("Uncertain", Status.Unknown), ("Errors", Status.Error), ("Cancelled", Status.Cancelled), ("Conflicts", Status.Conflict),
        };
        private readonly Button _pause;
        private readonly DispatcherTimer _timer;
        private string _lastSignature;
        private DateTime _lastDiskCheck = DateTime.MinValue;
        private DocWriteBlock _lastBlock;

        public static void Show(IntPtr owner)
        {
            if (_open != null) { _open._win.Activate(); return; }
            _open = new StatusWindow(owner);
            _open._win.Show();
        }

        private StatusWindow(IntPtr owner)
        {
            _win = Theme.NewWindow(Brand.Product, 760, 820);
            _win.ResizeMode = ResizeMode.CanResizeWithGrip;
            try { new WindowInteropHelper(_win) { Owner = owner }; } catch (Exception) { }

            var root = new DockPanel();
            var header = Theme.Header(Brand.Product, Brand.Subtitle, out _subtitle, out _pill, out _pillText);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Theme.Button leaves 8 px to the left of each button; the -8 aligns the first one with the footer margin.
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-8, 0, 0, 0) };
            var logs = Theme.Button("Open logs", Theme.Kind.Quiet);
            logs.Click += (s, e) => OpenFolder(Home.Root);
            var export = Theme.Button("Export CSV and JSONL", Theme.Kind.Quiet);
            export.Click += (s, e) => Export();
            left.Children.Add(logs);
            left.Children.Add(export);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            _pause = Theme.Button("Pause AI", Theme.Kind.Danger);
            _pause.Click += (s, e) => { Runtime.TogglePause(); Refresh(true); };
            var close = Theme.Button("Close");
            close.IsCancel = true;
            close.Click += (s, e) => _win.Close();
            right.Children.Add(_pause);
            right.Children.Add(close);
            var footerBorder = Theme.Footer(left, right);
            DockPanel.SetDock(footerBorder, Dock.Bottom);
            root.Children.Add(footerBorder);

            // Operations: header (scheduler and in progress), fixed filter and list. The filter is created once so focus is not lost on refresh.
            var ops = new StackPanel();
            ops.Children.Add(_opsHead);
            var seg = Theme.Segmented(OpsFilters.Select(f => f.Label).ToArray(), OpsFilters[0].Label,
                label => { _opsFilter = OpsFilters.First(f => f.Label == label).State; Refresh(true); });
            // Same RadioButtons in a WrapPanel so the six filters fit in narrow windows.
            var filters = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
            KeyboardNavigation.SetTabNavigation(filters, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(filters, KeyboardNavigationMode.Cycle);
            foreach (var r in seg.Children.OfType<RadioButton>().ToList()) { seg.Children.Remove(r); r.Margin = new Thickness(0, 0, 4, 4); filters.Children.Add(r); }
            ops.Children.Add(filters);
            ops.Children.Add(_opsList);

            var body = new StackPanel();
            body.Children.Add(Theme.Card(_hero));
            var details = new StackPanel();
            _agentsFold = AddFold(details, "Agents", "AI conversations connected to this Revit.", _agents);
            _opsFold = AddFold(details, "Long operations", "In progress and recent history, with a status filter.", ops);
            _safetyFold = AddFold(details, "Safety and recovery", null, _safety);
            _activityFold = AddFold(details, "Activity", "First the errors from the last 200 requests; then the last 30 actions, most recent on top.", _activity);
            AddFold(details, "Performance", "Last 200 requests.", _perf);
            AddFold(details, "C# engine", "Data measured in memory.", _engine);
            _systemFold = AddFold(details, "System", null, _system);
            body.Children.Add(Theme.Card(details, new Thickness(18, 10, 18, 10)));
            root.Children.Add(Theme.Scroll(body));
            _win.Content = root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += (s, e) => Refresh(false);
            _timer.Start();
            // No dangling references: on close the timer stops and the instance is released.
            _win.Closed += (s, e) => { _timer.Stop(); _open = null; };
            Refresh(true);
        }

        /// <summary>
        /// Collapsible section (closed the first time; afterwards as the person left it) with a hint on top and a warning chip next to the title, hidden unless needed.
        /// The content redraws on the timer without animation; only opening (a person's action or a new warning) animates.
        /// </summary>
        private static Fold AddFold(StackPanel host, string title, string hint, UIElement content)
        {
            var inner = new StackPanel();
            if (hint != null)
            {
                var h = Theme.Label(hint, 12, false, Theme.Muted);
                h.Margin = new Thickness(0, 0, 0, 8);
                inner.Children.Add(h);
            }
            inner.Children.Add(content);
            Expanded.TryGetValue(title, out var open);
            Attention.TryGetValue(title, out var seen);
            var d = Theme.Disclosure(title, inner, open);
            var f = new Fold { Title = title, Toggle = (ToggleButton)d.Children[0], Chip = Theme.Chip("", Theme.Amber), LastAttention = seen };
            f.Toggle.Checked += (s, e) => Expanded[title] = true;
            f.Toggle.Unchecked += (s, e) => Expanded[title] = false;
            f.Chip.Margin = new Thickness(10, 0, 0, 0);
            f.Chip.Visibility = Visibility.Collapsed;
            (f.Toggle.Content as Panel)?.Children.Add(f.Chip);
            if (host.Children.Count > 0)
            {
                var div = Theme.Divider();
                div.Margin = new Thickness(0, 6, 0, 6);
                host.Children.Add(div);
            }
            host.Children.Add(d);
            return f;
        }

        /// <summary>
        /// A section's warning chip (color + words; null = no chip). A new warning opens the section once; if the person
        /// collapses it, it stays collapsed until another warning appears. It never closes by itself. notify=false: informational chip (Slate), does not open.
        /// </summary>
        private static void Flag(Fold f, string text, Color color, bool notify = true)
        {
            var shown = text != null;
            var attention = shown && notify;
            f.Chip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown && f.Chip.Child is TextBlock tb)
            {
                tb.Text = text;
                tb.Foreground = Theme.B(Theme.Darken(color, 0.78));
                f.Chip.Background = Theme.Tint(color, 0x1C);
            }
            AutomationProperties.SetName(f.Toggle, shown ? f.Title + ", " + text : f.Title);
            if (attention && !f.LastAttention) f.Toggle.IsChecked = true;
            f.LastAttention = attention;
            Attention[f.Title] = attention;
        }

        private void Refresh(bool force)
        {
            if (!Runtime.Ready) return;
            var now = DateTime.UtcNow;
            var sessions = Runtime.Sessions?.All() ?? new List<Session>();
            var closed = Runtime.Sessions?.Closed() ?? new List<ClosedSession>();
            var activity = Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>();
            var paused = Runtime.Pause.On;
            // The queue on disk is re-read at most every 5 s (it is the only thing that touches disk).
            if (force || (now - _lastDiskCheck).TotalSeconds >= 5)
            {
                _lastDiskCheck = now;
                var key = Runtime.ActiveDocKey;
                try { _lastBlock = key == null ? null : Runtime.Plans.CheckWritable(key); }
                catch (Exception ex) { _lastBlock = new DocWriteBlock { DocKey = key }; _lastBlock.Errors.Add(ex.Message); }
            }
            (List<Job> Running, List<OperationHistory.Record> Recent, Dictionary<string, object> Scheduler) ops = (new List<Job>(), new List<OperationHistory.Record>(), null);
            string opsError = null;
            try { if (Runtime.Operations != null) ops = Runtime.Operations(_opsFilter, null); } catch (Exception ex) { opsError = ex.Message; }
            var opsSig = string.Join(",", ops.Running.Select(j => j.Id + j.Done + j.Phase)) + "/" + (ops.Recent.FirstOrDefault()?.JobId) + ops.Recent.Count + _opsFilter + opsError;
            var sig = opsSig + "§" + string.Join("|", sessions.Select(x => x.Id + x.Requests + x.Active + x.AgentName + AgentIdentity.Presence(x, now))) + "#" + closed.Count + "#" + paused + "#" +
                      activity.Count + (activity.LastOrDefault()?["t"]) + "#" + Runtime.ActiveDocTitle + "#" + Runtime.HeldCount + "#" + Runtime.BlockDynamicCode + "#" +
                      (_lastBlock?.Blocked ?? false) + "#" + (now - StartedUtc).TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "#" +
                      Runtime.Hold.On + Runtime.Config.Levels + "#" + Runtime.Journal?.Dropped + "/" + Runtime.Journal?.WriteErrors;
            if (!force && sig == _lastSignature) return;
            _lastSignature = sig;
            // The only focusable things rebuilt are the "Cancel" buttons of running operations: focus is restored by Tag.
            var focusTag = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;

            _subtitle.Text = Brand.Subtitle + " · Revit " + Runtime.RevitVersion + (string.IsNullOrEmpty(Runtime.ActiveDocTitle) ? " · no model" : " · " + Runtime.ActiveDocTitle);
            if (paused) Theme.SetPill(_pill, _pillText, Theme.Red, "AI paused", true);
            else if (_lastBlock != null && _lastBlock.Blocked) Theme.SetPill(_pill, _pillText, Theme.Amber, "Writing blocked", true);
            else if (sessions.Count == 0) Theme.SetPill(_pill, _pillText, Theme.Slate, "No agents", true);
            else Theme.SetPill(_pill, _pillText, Theme.Green, sessions.Count == 1 ? "1 agent" : sessions.Count + " agents", true);
            _pause.Content = paused ? "Resume AI" : "Pause AI";

            // Same rows for the figure, the chip and the Activity error list.
            var errorRows = activity.AsEnumerable().Reverse().Where(e => Str(e, "op") != "ping" && IsError(Str(e, "estado"))).ToList();
            var errors = errorRows.Count;
            FillHero(sessions, errors, now, paused);
            FillSystem();
            FillAgents(sessions, closed, now);
            FillOps(ops.Running, ops.Recent, ops.Scheduler, opsError);
            FillSafety();
            FillEngine();
            FillPerf(activity);
            FillActivity(activity, errorRows);

            var anonymous = sessions.Count(x => x.AgentName == null);
            Flag(_agentsFold, anonymous == 0 ? null : anonymous == 1 ? "1 without identity" : anonymous + " without identity", Theme.Amber);
            var review = ops.Recent.Count(r => r.Retry == "reconciliar_antes" || r.Retry == "revisar_diferencias");
            var inFlight = ops.Running.Count > 0 ? ops.Running.Count + " in progress" : null;
            if (opsError != null) Flag(_opsFold, "unavailable", Theme.Red);
            else if (review > 0) Flag(_opsFold, review + " to review" + (inFlight != null ? " · " + inFlight : ""), Theme.Amber);
            else Flag(_opsFold, inFlight, Theme.Slate, false); // work in progress: informs, does not demand attention
            Flag(_safetyFold, _lastBlock != null && _lastBlock.Blocked ? "document blocked" : null, Theme.Amber);
            Flag(_activityFold, errors == 0 ? null : (errors == 1 ? "1 error" : errors + " errors") + " in the last 200", Theme.Red);
            var journalIssues = Runtime.Journal != null && (Runtime.Journal.Dropped > 0 || Runtime.Journal.WriteErrors > 0);
            Flag(_systemFold, journalIssues ? "journal incomplete" : null, Theme.Amber);

            if (focusTag != null && focusTag.StartsWith(CancelTag, StringComparison.Ordinal) && !((Keyboard.FocusedElement as FrameworkElement)?.IsVisible ?? false))
                (FindTag(_opsHead, focusTag) ?? (FrameworkElement)_opsFold.Toggle).Focus();
        }

        /// <summary>
        /// At-a-glance answer: is BIMBRIDGE working and can the AI change my model right now? Mark + sentence, one line with Revit and the
        /// mode, the permissions and the figures the sentence does not say. No focusable controls, so it is rebuilt without losing anything and without animating.
        /// </summary>
        private void FillHero(List<Session> sessions, int errors, DateTime now, bool paused)
        {
            _hero.Children.Clear();
            var levels = Runtime.Config.Levels;
            var hold = Runtime.Hold.On || !levels.Contains("W");
            var running = sessions.Sum(x => x.Active);
            var active = sessions.Count(x => AgentIdentity.Presence(x, now) != "inactivo");
            var blocked = _lastBlock != null && _lastBlock.Blocked;

            // The agent count lives only here (there is no longer a "Connected agents" figure), also when paused or blocked.
            var agents = sessions.Count == 0 ? null : (sessions.Count == 1 ? "1 agent" : sessions.Count + " agents") + ", " + active + " active";
            string head; Color color;
            if (paused) { head = "AI paused" + (agents != null ? " · " + agents : ""); color = Theme.Red; }
            else if (blocked) { head = "Writing blocked on this model" + (agents != null ? " · " + agents : ""); color = Theme.Amber; }
            else if (agents == null) { head = "Ready, no agents connected"; color = Theme.Slate; }
            else { head = "Running · " + agents; color = Theme.Green; }

            string detail;
            if (paused) detail = "Revit rejects every AI request, reads included.";
            else
            {
                detail = (string.IsNullOrEmpty(Runtime.ActiveDocTitle) ? "No model open"
                          : running > 0 ? "Revit serving requests" : "Revit idle") + " · " +
                         (hold ? "Hold: changes wait in the queue until you apply them."
                               : "Live: what you approve in the conversation is applied immediately.");
                if (blocked) detail = "There are operations to reconcile; the AI cannot write until they are resolved (see Safety and recovery). " + detail;
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement mark = color == Theme.Green ? Theme.ResultMark(Theme.Green, true, 18) : (FrameworkElement)Theme.Dot(color, 10);
            mark.VerticalAlignment = VerticalAlignment.Top;
            mark.HorizontalAlignment = HorizontalAlignment.Left;
            mark.Margin = new Thickness(color == Theme.Green ? 0 : 4, 5, 0, 0);
            row.Children.Add(mark);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = head, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Ink), TextWrapping = TextWrapping.Wrap });
            var d = Theme.Label(detail, 12.5, false, Theme.Muted);
            d.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(d);
            var perms = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            perms.Children.Add(Perm("Read", levels.Contains("R")));
            perms.Children.Add(Perm("Prepare", levels.Contains("P")));
            perms.Children.Add(Perm("Apply", levels.Contains("W")));
            perms.Children.Add(Perm(Runtime.BlockDynamicCode ? "Dynamic C# blocked" : "Dynamic C# allowed", !Runtime.BlockDynamicCode));
            text.Children.Add(perms);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _hero.Children.Add(row);

            // Only figures the sentence does not already say (agents are in the sentence and in the pill).
            var stats = new UniformGrid { Columns = 3, Margin = new Thickness(28, 14, 0, 0) };
            AddStat(stats, Runtime.HeldCount, "Plans in the queue", "for this model", null);
            AddStat(stats, errors, "Errors", "in the last 200", errors > 0 ? Theme.Darken(Theme.Red, 0.8) : (Color?)null);
            if (running > 0) AddStat(stats, running, "Requests running", null, null);
            _hero.Children.Add(stats);
        }

        private static void AddStat(Panel host, long value, string label, string sub, Color? color)
        {
            var s = Theme.Stat(value.ToString(CultureInfo.InvariantCulture), label, sub, out var v);
            if (color != null) v.Foreground = Theme.B(color.Value);
            s.Margin = new Thickness(0, 0, 12, 0);
            host.Children.Add(s);
        }

        private static FrameworkElement FindTag(DependencyObject root, string tag)
        {
            foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (c is FrameworkElement f && tag.Equals(f.Tag as string)) return f;
                var r = FindTag(c, tag);
                if (r != null) return r;
            }
            return null;
        }

        private void FillSystem()
        {
            _system.Children.Clear();
            var levels = Runtime.Config.Levels;
            Kv("Version", AddinVersion());
            Kv("Revit", Runtime.RevitVersion + " · pid " + Process.GetCurrentProcess().Id);
            Kv("Active document", string.IsNullOrEmpty(Runtime.ActiveDocTitle) ? "none" : Runtime.ActiveDocTitle);
            Kv("Mode", Runtime.Pause.On ? "paused" : (Runtime.Hold.On || !levels.Contains("W")) ? "Hold" : "Live");
            Kv("Journal", Runtime.Journal == null ? "unavailable" : "lines lost " + Runtime.Journal.Dropped + " · write errors " + Runtime.Journal.WriteErrors);
            Kv("Active since", Theme.Ago(StartedUtc));
        }

        private void FillAgents(List<Session> sessions, List<ClosedSession> closed, DateTime now)
        {
            _agents.Children.Clear();
            if (sessions.Count == 0)
                _agents.Children.Add(Theme.Label("No agent connected. When Claude or Codex open a conversation with the MCP they will appear here; " +
                                                 "with ctx agente=\"name\" they identify themselves with the chat title.", 13, false, Theme.Muted));
            foreach (var s in sessions.OrderBy(x => x.ConnectedUtc))
            {
                if (_agents.Children.Count > 0) _agents.Children.Add(Theme.Divider());
                var card = new StackPanel();
                var head = new WrapPanel();
                var presence = AgentIdentity.Presence(s, now);
                var dot = Theme.Dot(presence == "ejecutando" ? Theme.Amber : presence == "activo" ? Theme.Green : Theme.Slate);
                dot.Margin = new Thickness(0, 0, 8, 0);
                head.Children.Add(dot);
                head.Children.Add(Theme.Chip(s.DisplayName, s.AgentName == null ? Theme.Slate : Theme.ClientColor(s.ClientName)));
                head.Children.Add(new TextBlock
                {
                    Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                    Text = presence + " · " + Theme.ClientName(s.ClientName) + (s.ClientVersion != null ? " " + s.ClientVersion : "") +
                           " · identity " + (s.AgentName == null ? "insufficient: no reads or changes until it registers" + (s.IdentityNote != null ? " (" + s.IdentityNote + ")" : "") : "declarada (" + s.IdentitySource + ") · " + s.AgentId) +
                           (s.Principal != null ? " · user " + s.Principal + " (verified)" : ""),
                    Foreground = Theme.B(presence == "inactivo" ? Theme.Muted : Theme.Ink), VerticalAlignment = VerticalAlignment.Center,
                });
                card.Children.Add(head);
                var detail = "session " + s.Id + " · connected " + Theme.Ago(s.ConnectedUtc) + " · last activity " + Theme.Ago(s.LastSeenUtc) +
                             " · " + s.Requests + " requests · " + s.Active + " running · " + s.Errors + " errors · " + FormatMs(s.TotalMs) + " total" +
                             (s.LastOp != null ? " · last: " + s.LastOp : "") + (s.LastDoc != null ? " on " + s.LastDoc : "") +
                             (s.Conversation != null ? " · conversation " + s.Conversation : "") + (s.Model != null ? " · model " + s.Model : "");
                card.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(16, 6, 0, 0) });
                _agents.Children.Add(card);
            }
            var recent = closed.AsEnumerable().Reverse().Take(8).ToList();
            if (recent.Count > 0)
            {
                var title = Theme.Label("Disconnected in this Revit session", 12, true, Theme.Muted);
                title.Margin = new Thickness(0, 16, 0, 4);
                _agents.Children.Add(title);
                foreach (var c in recent)
                    _agents.Children.Add(new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = (c.AgentName ?? "(unnamed) · " + c.ClientName) + " · " + (c.Reason == "conexion_cerrada" ? "connection closed" : c.Reason) +
                               " " + Theme.Ago(c.ClosedUtc) + " · " + c.Requests + " requests, " + c.Errors + " errors",
                        FontSize = 12, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(2, 2, 0, 0),
                    });
            }
        }

        /// <summary>
        /// #5 (0.9.1): long operations in progress (with Cancel) and recent history with a status filter and retry advice.
        /// It is the person's panel in their Revit: it shows all sessions. Cancel asks for confirmation and only signals the job's
        /// token; if changes had already been applied, the job ends as cancelled and the history advises reconciling.
        /// </summary>
        private void FillOps(List<Job> running, List<OperationHistory.Record> recent, Dictionary<string, object> scheduler, string error)
        {
            _opsHead.Children.Clear();
            _opsList.Children.Clear();
            if (error != null) { Kv("Status", "unavailable: " + error, _opsHead); return; }
            if (scheduler != null)
                Kv("Scheduler", "in progress " + scheduler["en_curso"] + " of " + scheduler["max_en_curso"] + " · queued " + scheduler["en_cola"] + " · max per agent " + scheduler["max_por_agente"] +
                                   " · rejected because the queue was full " + scheduler["rechazadas_por_cola_llena"], _opsHead);
            if (running.Count == 0) Kv("In progress", "none", _opsHead);
            foreach (var j in running)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock
                {
                    Text = j.Id + " · " + j.Kind + (j.Total > 0 ? " · " + j.Done + "/" + j.Total : "") + (string.IsNullOrEmpty(j.Phase) ? "" : " · " + j.Phase) + " · " + Theme.Ago(j.CreatedUtc),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(Theme.Ink), VerticalAlignment = VerticalAlignment.Center,
                });
                var cancel = Theme.Button("Cancel", Theme.Kind.Quiet);
                var id = j.Id; var kind = j.Kind;
                cancel.Tag = CancelTag + id;
                AutomationProperties.SetName(cancel, "Cancel " + id);
                cancel.Click += (s, e) =>
                {
                    if (MessageBox.Show("Cancel " + id + " (" + kind + ")?\n\nIf it already applied changes, they will stay as they are and the model will need checking.", Brand.Product, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    bool ok = false;
                    try { ok = Runtime.CancelOperation != null && Runtime.CancelOperation(id); } catch (Exception ex) { MessageBox.Show("Could not cancel: " + ex.Message, Brand.Product); }
                    if (!ok) MessageBox.Show("The job had already finished.", Brand.Product);
                    Refresh(true);
                };
                Grid.SetColumn(cancel, 1);
                g.Children.Add(cancel);
                _opsHead.Children.Add(g);
            }
            if (recent.Count == 0) _opsList.Children.Add(Theme.Label("No finished operations with this filter.", 13, false, Theme.Muted));
            foreach (var r in recent)
            {
                var bad = r.Retry == "reconciliar_antes" || r.Retry == "revisar_diferencias";
                var advice = r.Retry == "no_necesario" ? "" : r.Retry == "reintentable" ? " · can be repeated (nothing was applied)"
                           : r.Retry == "revisar_diferencias" ? " · REVIEW: applied but does not match what was requested" : " · CHECK the model before repeating";
                _opsList.Children.Add(new TextBlock
                {
                    Text = r.FinishedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · " + r.JobId + " · " + r.Kind + " · " + r.State + " · " + FormatMs(r.Ms) + advice +
                           (string.IsNullOrEmpty(r.Error) ? "" : " · " + r.Error),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(bad ? Theme.Darken(Theme.Amber, 0.75) : Theme.Ink), Margin = new Thickness(2, 1, 0, 1),
                });
            }
        }

        private void FillSafety()
        {
            _safety.Children.Clear();
            Kv("Block C# code injection", Runtime.BlockDynamicCode ? "on: exec rejected" : "off: exec allowed for authorized local agents", _safety);
            if (_lastBlock == null) Kv("Active document writing", "no document", _safety);
            else if (!_lastBlock.Blocked) Kv("Active document writing", "no operations pending reconciliation", _safety);
            else
            {
                var notice = Theme.Notice("Document blocked: " + _lastBlock.Message, Theme.Amber, out _);
                notice.Margin = new Thickness(0, 0, 0, 6);
                _safety.Children.Add(notice);
                var how = Theme.Label("The AI resolves it with job op=reconciliar plan_id=…; if it cannot be proven, a person with Revit closed uses AgentHost plan-store.", 12, false, Theme.Muted);
                how.Margin = new Thickness(0, 0, 0, 10);
                _safety.Children.Add(how);
            }
            var execs = (Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>()).Where(e => Str(e, "op") == "exec" || Str(e, "op") == "exec_codigo").Reverse().Take(5).ToList();
            Kv("Latest dynamic runs", execs.Count == 0 ? "none" : string.Join(" · ", execs.Select(e => (Str(e, "agente") ?? "unnamed") + " " + Str(e, "estado"))), _safety);
        }

        /// <summary>
        /// #5: C# engine with data measured in memory (Revit thread, single-flight compilation cache, query snapshots,
        /// background jobs). Rust and Python are queried with the power cap tool: the panel does not start them just to look.
        /// </summary>
        private void FillEngine()
        {
            _engine.Children.Clear();
            Dictionary<string, object> d = null;
            try { d = Runtime.EngineDiagnostics?.Invoke(); } catch (Exception ex) { Kv("Status", "unavailable: " + ex.Message, _engine); return; }
            if (d == null) { Kv("Status", "unavailable", _engine); return; }
            foreach (var kv in d)
            {
                var label = kv.Key.Replace('_', ' ');
                var text = kv.Value is Dictionary<string, object> sub
                    ? string.Join(" · ", sub.Select(x => x.Key.Replace('_', ' ') + " " + Convert.ToString(x.Value, CultureInfo.InvariantCulture)))
                    : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                Kv(char.ToUpperInvariant(label[0]) + label.Substring(1), string.IsNullOrEmpty(text) ? "—" : text, _engine);
            }
            Kv("Rust / Python", "queried with the power cap tool (the panel does not start workers just to look)", _engine);
        }

        private void FillPerf(List<Dictionary<string, object>> activity)
        {
            _perf.Children.Clear();
            var groups = activity.Where(e => Str(e, "op") != "ping" && Str(e, "op") != "conectar" && Str(e, "op") != "desconectar" && e.ContainsKey("ms"))
                .GroupBy(e => Str(e, "op")).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) { _perf.Children.Add(Theme.Label("No requests yet.", 13, false, Theme.Muted)); return; }
            // Table: operation on the left, figures right-aligned with fixed-width digits.
            var table = new Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var w in new[] { 56.0, 84.0, 84.0, 72.0 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            void Cell(int r, int c, string text, Color color, bool head = false)
            {
                var tb = new TextBlock
                {
                    Text = text, FontSize = head ? 11.5 : 12, FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Theme.B(color),
                    TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 4),
                };
                Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
                Grid.SetRow(tb, r); Grid.SetColumn(tb, c);
                table.Children.Add(tb);
            }
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border { BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetColumnSpan(rule, 5);
            table.Children.Add(rule);
            var heads = new[] { "Operation", "n", "Median", "p95", "Errors" };
            for (var c = 0; c < heads.Length; c++) Cell(0, c, heads[c], Theme.Muted, true);
            foreach (var g in groups)
            {
                var ms = g.Select(e => Convert.ToInt64(e["ms"], CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
                var med = ms[(ms.Count - 1) / 2];
                var p95 = ms.Count >= 20 ? FormatMs(ms[(int)Math.Ceiling(ms.Count * 0.95) - 1]) : "—";
                var err = g.Count(e => IsError(Str(e, "estado")));
                var r = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(r, 0, g.Key, Theme.Ink);
                Cell(r, 1, ms.Count.ToString(CultureInfo.InvariantCulture), Theme.Ink);
                Cell(r, 2, FormatMs(med), Theme.Ink);
                Cell(r, 3, p95, Theme.Ink);
                Cell(r, 4, err.ToString(CultureInfo.InvariantCulture), err > 0 ? Theme.Darken(Theme.Red, 0.8) : Theme.Muted);
            }
            _perf.Children.Add(table);
            var notes = Theme.Label("p95 only with 20 or more samples. Tokens: the MCP client does not report them; estimates are in the metricas tool.", 12, false, Theme.Muted);
            notes.Margin = new Thickness(0, 10, 0, 0);
            _perf.Children.Add(notes);
        }

        private void FillActivity(List<Dictionary<string, object>> activity, List<Dictionary<string, object>> errorRows)
        {
            _activity.Children.Clear();
            var items = activity.AsEnumerable().Reverse().Where(e => Str(e, "op") != "ping").Take(30).ToList();
            if (items.Count == 0) { _activity.Children.Add(Theme.Label("No activity yet in this Revit session.", 13, false, Theme.Muted)); return; }
            // Errors from the 200 go first so the "N errors" chip always matches what is shown.
            if (errorRows.Count > 0)
            {
                _activity.Children.Add(Theme.Label(errorRows.Count == 1 ? "1 error in the last 200" : errorRows.Count + " errors in the last 200", 12, true, Theme.Muted));
                foreach (var e in errorRows) _activity.Children.Add(ActivityRow(e));
                var recent = Theme.Label("Last 30 actions", 12, true, Theme.Muted);
                recent.Margin = new Thickness(0, 14, 0, 0);
                _activity.Children.Add(recent);
            }
            foreach (var e in items) _activity.Children.Add(ActivityRow(e));
        }

        private void Kv(string k, string v, StackPanel target = null)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = k, Foreground = Theme.B(Theme.Muted), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
            var tv = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(Theme.Ink) };
            Grid.SetColumn(tv, 1);
            g.Children.Add(tv);
            (target ?? _system).Children.Add(g);
        }

        private static UIElement Perm(string text, bool on)
        {
            var b = Theme.Chip((on ? "✓ " : "✕ ") + text, on ? Theme.Green : Theme.Slate);
            b.Margin = new Thickness(0, 0, 8, 4);
            return b;
        }

        private static string Str(Dictionary<string, object> e, string k) =>
            e.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        private static bool IsError(string status) => status == Status.Error || status == Status.Unknown;

        private static string FormatMs(long ms) => ms < 1000 ? ms + " ms" : (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

        private static string AddinVersion()
        {
            try
            {
                var a = typeof(StatusWindow).Assembly;
                var info = a.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault();
                return info?.InformationalVersion ?? a.GetName().Version.ToString();
            }
            catch (Exception) { return "?"; }
        }

        private static UIElement ActivityRow(Dictionary<string, object> e)
        {
            var author = Str(e, "autor") ?? "";
            var agent = Str(e, "agente");
            var op = Str(e, "op") ?? "";
            var status = Str(e, "estado") ?? "";
            var ms = e.TryGetValue("ms", out var m) ? Convert.ToInt64(m, CultureInfo.InvariantCulture) : 0;
            DateTime t;
            DateTime.TryParse(Str(e, "t"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var time = new TextBlock { Text = t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Foreground = Theme.B(Theme.Muted), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            grid.Children.Add(time);

            var human = author == VersionRegistry.HumanAuthor;
            var who = human ? "You" : agent ?? Theme.ClientName(Str(e, "cliente") ?? author) + " (unnamed)";
            var chip = Theme.Chip(who, human ? Theme.Navy : Theme.ClientColor(Str(e, "cliente") ?? author));
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);

            var (text, color) = Describe(op, status, ms);
            var doc = Str(e, "doc");
            var tb = new TextBlock { Text = text + (doc != null ? " · " + doc : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.B(color) };
            Grid.SetColumn(tb, 2);
            grid.Children.Add(tb);
            return grid;
        }

        private static (string, Color) Describe(string op, string status, long ms)
        {
            var dur = ms > 0 ? " (" + FormatMs(ms) + ")" : "";
            if (status == Status.RevitBusy) return ("Revit was busy; the agent can retry", Theme.Darken(Theme.Amber, 0.75));
            if (status == Status.Unknown) return (op + ": UNKNOWN result, reconciliation required" + dur, Theme.Darken(Theme.Red, 0.8));
            if (status == Status.Rejected && op != "commit") return (op + " rejected (pause, permissions or block)", Theme.Darken(Theme.Red, 0.8));
            if (status == Status.Invalid) return (op + ": malformed request; how to fix it was explained", Theme.Muted);
            if (status == Status.Error) return (op + ": error; see log", Theme.Darken(Theme.Red, 0.8));
            switch (op)
            {
                case "conectar": return ("Connected", Theme.Ink);
                case "desconectar": return ("Disconnected", Theme.Muted);
                case "context": return ("Checked Revit status" + dur, Theme.Ink);
                case "query": case "get": return ("Query" + dur, Theme.Ink);
                case "check": return ("Audit" + dur, Theme.Ink);
                case "sets": return ("Reviewed its sets", Theme.Ink);
                case "plan": return ("Prepared a plan" + dur, Theme.Ink);
                case "job": return ("Checked a job or plan", Theme.Muted);
                case "exec": return ("Dynamic C# " + status + dur, Theme.Ink);
                case "commit":
                    if (status == Status.Held) return ("Left a change in the queue", Theme.Darken(Theme.Amber, 0.75));
                    if (status == Status.Ok) return ("Applied a change" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (status == PlanStatus.Trialed) return ("Trial: applied, verified and rolled back" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (status == Status.Conflict) return ("Change not applied: the model had changed", Theme.Darken(Theme.Amber, 0.75));
                    return ("Change not applied (" + status + ")", Theme.Darken(Theme.Red, 0.8));
                case "aplicar_cola":
                    if (status == PlanStatus.Applied) return ("You applied a plan from the queue" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (status == PlanStatus.Conflict) return ("A queued plan was skipped: the model had changed", Theme.Darken(Theme.Amber, 0.75));
                    return ("Queued plan: " + status, Theme.Darken(Theme.Red, 0.8));
                case "retener": return (status == "si" ? "Turned on Hold mode" : "Went back to Live mode", Theme.Ink);
                case "pausar": return (status == "si" ? "Paused the AI" : "Resumed the AI", Theme.Ink);
                case "descartar":
                case "descartar_cola": return ("Discarded changes from the queue", Theme.Muted);
                default: return (op + " · " + status + dur, Theme.Muted);
            }
        }

        /// <summary>Exports recent activity (JSONL as is and CSV) and a snapshot of the sessions to %LOCALAPPDATA%\Bimbridge\exportes.</summary>
        private void Export()
        {
            try
            {
                var dir = Path.Combine(Home.Root, "exportes");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var recent = Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>();
                File.WriteAllLines(Path.Combine(dir, "actividad_" + stamp + ".jsonl"), recent.Select(e => Json.Write(e)));
                var csv = new StringBuilder("t,agente,cliente,op,status,ms,doc,traza\n");
                foreach (var e in recent)
                    csv.Append(string.Join(",", new[] { "t", "agente", "cliente", "op", "estado", "ms", "doc", "traza" }.Select(k => Csv(Str(e, k))))).Append('\n');
                File.WriteAllText(Path.Combine(dir, "actividad_" + stamp + ".csv"), csv.ToString(), new UTF8Encoding(true));
                var now = DateTime.UtcNow;
                var sessions = (Runtime.Sessions?.All() ?? new List<Session>()).Select(s => (object)new Dictionary<string, object>
                {
                    ["sesion"] = s.Id, ["agente"] = s.AgentName, ["fuente_identidad"] = s.IdentitySource, ["cliente"] = s.ClientName, ["version_cliente"] = s.ClientVersion,
                    ["principal"] = s.Principal, ["estado"] = AgentIdentity.Presence(s, now), ["solicitudes"] = s.Requests, ["errores"] = s.Errors, ["en_ejecucion"] = s.Active,
                    ["ms_acumulados"] = s.TotalMs, ["conectada"] = s.ConnectedUtc.ToString("o", CultureInfo.InvariantCulture), ["ultima_actividad"] = s.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture),
                }).ToList();
                File.WriteAllText(Path.Combine(dir, "sesiones_" + stamp + ".json"), Json.Write(new Dictionary<string, object> { ["sesiones"] = sessions }));
                OpenFolder(dir);
            }
            catch (Exception ex) { MessageBox.Show("Could not export: " + ex.Message, Brand.Product); }
        }

        private static string Csv(string v) => v == null ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";

        private static void OpenFolder(string dir)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception) { }
        }
    }
}
