// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

namespace PDI.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Centro de control «BIMBRIDGE - Power Engine» (no modal): resumen, agentes con nombre, actividad atribuida, seguridad y recuperación,
    /// rendimiento y exportación. Sólo lee estado en memoria y el disco de la cola; nunca llama a la API de Revit ni retiene Document/Element,
    /// así que puede quedar abierto mientras se trabaja. Se redibuja como mucho cada 1,5 s y sólo si algo cambió.
    /// </summary>
    internal sealed class StatusWindow
    {
        private static StatusWindow _open;
        private static DateTime StartedUtc => Runtime.StartedUtc;
        private readonly Window _win;
        private readonly TextBlock _subtitle, _pillText;
        private readonly Border _pill;
        private readonly StackPanel _summary = new StackPanel(), _agents = new StackPanel(), _activity = new StackPanel(),
            _safety = new StackPanel(), _perf = new StackPanel();
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

            var footer = new Grid { Margin = new Thickness(24, 12, 24, 14) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 0) };
            var logs = Theme.Button("Abrir registros", Theme.Kind.Quiet);
            logs.Click += (s, e) => OpenFolder(Home.Root);
            var export = Theme.Button("Exportar CSV y JSONL", Theme.Kind.Quiet);
            export.Click += (s, e) => Export();
            left.Children.Add(logs);
            left.Children.Add(export);
            footer.Children.Add(left);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            _pause = Theme.Button("Pausar IA", Theme.Kind.Danger);
            _pause.Click += (s, e) => { Runtime.TogglePause(); Refresh(true); };
            var close = Theme.Button("Cerrar");
            close.Click += (s, e) => _win.Close();
            right.Children.Add(_pause);
            right.Children.Add(close);
            Grid.SetColumn(right, 1);
            footer.Children.Add(right);
            var footerBorder = new Border { Background = Theme.B(Theme.White), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
            DockPanel.SetDock(footerBorder, Dock.Bottom);
            root.Children.Add(footerBorder);

            var body = new StackPanel();
            body.Children.Add(Section("Resumen", _summary));
            body.Children.Add(Section("Agentes", _agents));
            body.Children.Add(Section("Seguridad y recuperación", _safety));
            body.Children.Add(Section("Rendimiento (últimas 200 solicitudes)", _perf));
            body.Children.Add(Section("Actividad", _activity));
            root.Children.Add(Theme.Scroll(body));
            _win.Content = root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _timer.Tick += (s, e) => Refresh(false);
            _timer.Start();
            // Sin referencias colgando: al cerrar se para el temporizador y se suelta la instancia.
            _win.Closed += (s, e) => { _timer.Stop(); _open = null; };
            Refresh(true);
        }

        private static UIElement Section(string title, UIElement content)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(2, 6, 0, 6) });
            s.Children.Add(Theme.Card(content));
            return s;
        }

        private void Refresh(bool force)
        {
            if (!Runtime.Ready) return;
            var now = DateTime.UtcNow;
            var sessions = Runtime.Sessions?.All() ?? new List<Session>();
            var closed = Runtime.Sessions?.Closed() ?? new List<ClosedSession>();
            var activity = Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>();
            var paused = Runtime.Pause.On;
            // La cola en disco se relee cada 5 s como mucho (es lo único que toca disco).
            if (force || (now - _lastDiskCheck).TotalSeconds >= 5)
            {
                _lastDiskCheck = now;
                var key = Runtime.ActiveDocKey;
                try { _lastBlock = key == null ? null : Runtime.Plans.CheckWritable(key); }
                catch (Exception ex) { _lastBlock = new DocWriteBlock { DocKey = key }; _lastBlock.Errors.Add(ex.Message); }
            }
            var sig = string.Join("|", sessions.Select(x => x.Id + x.Requests + x.Active + x.AgentName + AgentIdentity.Presence(x, now))) + "#" + closed.Count + "#" + paused + "#" +
                      activity.Count + (activity.LastOrDefault()?["t"]) + "#" + Runtime.ActiveDocTitle + "#" + Runtime.HeldCount + "#" + Runtime.BlockDynamicCode + "#" +
                      (_lastBlock?.Blocked ?? false) + "#" + (now - StartedUtc).TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
            if (!force && sig == _lastSignature) return;
            _lastSignature = sig;

            _subtitle.Text = Brand.Subtitle + " · Revit " + Runtime.RevitVersion + (string.IsNullOrEmpty(Runtime.ActiveDocTitle) ? " · sin modelo" : " · " + Runtime.ActiveDocTitle);
            if (paused) SetPill("IA en pausa", Theme.Red);
            else if (_lastBlock != null && _lastBlock.Blocked) SetPill("Escritura bloqueada", Theme.Amber);
            else if (sessions.Count == 0) SetPill("Sin agentes", Theme.Slate);
            else SetPill(sessions.Count == 1 ? "1 agente" : sessions.Count + " agentes", Theme.Green);
            _pause.Content = paused ? "Reanudar IA" : "Pausar IA";

            FillSummary(sessions, activity, now);
            FillAgents(sessions, closed, now);
            FillSafety();
            FillPerf(activity);
            FillActivity(activity);
        }

        private void FillSummary(List<Session> sessions, List<Dictionary<string, object>> activity, DateTime now)
        {
            _summary.Children.Clear();
            var levels = Runtime.Config.Levels;
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(Perm("Leer", levels.Contains("R")));
            row.Children.Add(Perm("Preparar", levels.Contains("P")));
            row.Children.Add(Perm("Aplicar", levels.Contains("W")));
            row.Children.Add(Perm(Runtime.BlockDynamicCode ? "C# dinámico bloqueado" : "C# dinámico permitido", !Runtime.BlockDynamicCode));
            _summary.Children.Add(row);
            var running = sessions.Sum(x => x.Active);
            var errors = activity.Count(e => IsError(Str(e, "estado")));
            Kv("Versión", AddinVersion());
            Kv("Revit", Runtime.RevitVersion + " · pid " + Process.GetCurrentProcess().Id);
            Kv("Documento activo", string.IsNullOrEmpty(Runtime.ActiveDocTitle) ? "ninguno" : Runtime.ActiveDocTitle);
            Kv("Agentes conectados", sessions.Count + " (" + sessions.Count(x => AgentIdentity.Presence(x, now) != "inactivo") + " activos)");
            Kv("Solicitudes en ejecución", running.ToString(CultureInfo.InvariantCulture));
            Kv("Planes en la cola de este modelo", Runtime.HeldCount.ToString(CultureInfo.InvariantCulture));
            Kv("Errores en las últimas 200", errors.ToString(CultureInfo.InvariantCulture));
            Kv("Modo", Runtime.Pause.On ? "pausa" : (Runtime.Hold.On || !levels.Contains("W")) ? "Retener" : "En vivo");
            Kv("Diario", Runtime.Journal == null ? "no disponible" : "líneas perdidas " + Runtime.Journal.Dropped + " · errores de escritura " + Runtime.Journal.WriteErrors);
            Kv("Activo desde", Theme.Ago(StartedUtc));
        }

        private void FillAgents(List<Session> sessions, List<ClosedSession> closed, DateTime now)
        {
            _agents.Children.Clear();
            if (sessions.Count == 0)
                _agents.Children.Add(Theme.Label("Ningún agente conectado. Cuando Claude o Codex abran una conversación con el MCP aparecerán aquí; " +
                                                 "con ctx agente=\"nombre\" se identifican con el título del chat.", 13, false, Theme.Muted));
            foreach (var s in sessions.OrderBy(x => x.ConnectedUtc))
            {
                var card = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
                var head = new WrapPanel();
                head.Children.Add(Theme.Chip(s.DisplayName, s.AgentName == null ? Theme.Slate : Theme.ClientColor(s.ClientName)));
                var presence = AgentIdentity.Presence(s, now);
                head.Children.Add(new TextBlock
                {
                    Text = "  " + presence + " · " + Theme.ClientName(s.ClientName) + (s.ClientVersion != null ? " " + s.ClientVersion : "") +
                           " · identidad " + (s.AgentName == null ? "insuficiente" : "declarada (" + s.IdentitySource + ")") +
                           (s.Principal != null ? " · usuario " + s.Principal + " (verificado)" : ""),
                    Foreground = Theme.B(presence == "inactivo" ? Theme.Muted : Theme.Ink), VerticalAlignment = VerticalAlignment.Center,
                });
                card.Children.Add(head);
                var detail = "sesión " + s.Id + " · conectada " + Theme.Ago(s.ConnectedUtc) + " · última actividad " + Theme.Ago(s.LastSeenUtc) +
                             " · " + s.Requests + " solicitudes · " + s.Active + " en ejecución · " + s.Errors + " errores · " + FormatMs(s.TotalMs) + " acumulados" +
                             (s.LastOp != null ? " · última: " + s.LastOp : "") + (s.LastDoc != null ? " en " + s.LastDoc : "") +
                             (s.Conversation != null ? " · conversación " + s.Conversation : "") + (s.Model != null ? " · modelo " + s.Model : "");
                card.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(2, 2, 0, 0) });
                _agents.Children.Add(card);
            }
            var recent = closed.AsEnumerable().Reverse().Take(8).ToList();
            if (recent.Count > 0)
            {
                _agents.Children.Add(Theme.Label("Desconectados en esta sesión de Revit", 12, true, Theme.Muted));
                foreach (var c in recent)
                    _agents.Children.Add(new TextBlock
                    {
                        Text = (c.AgentName ?? "(sin nombre) · " + c.ClientName) + " · " + (c.Reason == "conexion_cerrada" ? "conexión cerrada" : c.Reason) +
                               " " + Theme.Ago(c.ClosedUtc) + " · " + c.Requests + " solicitudes, " + c.Errors + " errores",
                        FontSize = 12, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(2, 2, 0, 0),
                    });
            }
        }

        private void FillSafety()
        {
            _safety.Children.Clear();
            Kv("Bloquear inyección de código C#", Runtime.BlockDynamicCode ? "activado: exec rechazado" : "desactivado: exec permitido para agentes locales autorizados", _safety);
            if (_lastBlock == null) Kv("Escritura del documento activo", "sin documento", _safety);
            else if (!_lastBlock.Blocked) Kv("Escritura del documento activo", "sin operaciones pendientes de reconciliar", _safety);
            else
            {
                _safety.Children.Add(Theme.Label("Documento bloqueado: " + _lastBlock.Message, 13, true, Theme.Darken(Theme.Amber, 0.75)));
                _safety.Children.Add(Theme.Label("La IA lo resuelve con job op=reconciliar plan_id=…; si no se puede demostrar, una persona con Revit cerrado usa AgentHost plan-store.", 12, false, Theme.Muted));
            }
            var execs = (Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>()).Where(e => Str(e, "op") == "exec" || Str(e, "op") == "exec_codigo").Reverse().Take(5).ToList();
            Kv("Últimas ejecuciones dinámicas", execs.Count == 0 ? "ninguna" : string.Join(" · ", execs.Select(e => (Str(e, "agente") ?? "sin nombre") + " " + Str(e, "estado"))), _safety);
        }

        private void FillPerf(List<Dictionary<string, object>> activity)
        {
            _perf.Children.Clear();
            var groups = activity.Where(e => Str(e, "op") != "ping" && Str(e, "op") != "conectar" && Str(e, "op") != "desconectar" && e.ContainsKey("ms"))
                .GroupBy(e => Str(e, "op")).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) { _perf.Children.Add(Theme.Label("Sin solicitudes todavía.", 13, false, Theme.Muted)); return; }
            _perf.Children.Add(Theme.Label("operación · n · mediana · p95 · errores (p95 sólo con 20 o más muestras)", 12, true, Theme.Muted));
            foreach (var g in groups)
            {
                var ms = g.Select(e => Convert.ToInt64(e["ms"], CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
                var med = ms[(ms.Count - 1) / 2];
                var p95 = ms.Count >= 20 ? FormatMs(ms[(int)Math.Ceiling(ms.Count * 0.95) - 1]) : "—";
                var err = g.Count(e => IsError(Str(e, "estado")));
                _perf.Children.Add(new TextBlock { Text = g.Key + " · " + ms.Count + " · " + FormatMs(med) + " · " + p95 + " · " + err, FontSize = 12, Foreground = Theme.B(Theme.Ink), Margin = new Thickness(2, 1, 0, 1) });
            }
            _perf.Children.Add(Theme.Label("Tokens: el cliente MCP no los informa; los estimados están en la herramienta metricas.", 12, false, Theme.Muted));
        }

        private void FillActivity(List<Dictionary<string, object>> activity)
        {
            _activity.Children.Clear();
            var items = activity.AsEnumerable().Reverse().Where(e => Str(e, "op") != "ping").Take(30).ToList();
            if (items.Count == 0) _activity.Children.Add(Theme.Label("Todavía no hay actividad en esta sesión de Revit.", 13, false, Theme.Muted));
            foreach (var e in items) _activity.Children.Add(ActivityRow(e));
        }

        private void Kv(string k, string v, StackPanel target = null)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = k, Foreground = Theme.B(Theme.Muted), FontSize = 12 });
            var tv = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.B(Theme.Ink) };
            Grid.SetColumn(tv, 1);
            g.Children.Add(tv);
            (target ?? _summary).Children.Add(g);
        }

        private void SetPill(string text, Color c)
        {
            _pillText.Text = text;
            _pill.Background = Theme.Tint(c, c == Theme.Slate ? (byte)0x70 : (byte)0xC0);
        }

        private static UIElement Perm(string text, bool on)
        {
            var b = Theme.Chip((on ? "✓ " : "✕ ") + text, on ? Theme.Green : Theme.Slate);
            b.Margin = new Thickness(0, 0, 8, 4);
            return b;
        }

        private static string Str(Dictionary<string, object> e, string k) =>
            e.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        private static bool IsError(string estado) => estado == Status.Error || estado == Status.Desconocido;

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
            var estado = Str(e, "estado") ?? "";
            var ms = e.TryGetValue("ms", out var m) ? Convert.ToInt64(m, CultureInfo.InvariantCulture) : 0;
            DateTime t;
            DateTime.TryParse(Str(e, "t"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Foreground = Theme.B(Theme.Muted), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var human = author == VersionRegistry.HumanAuthor;
            var who = human ? "Usted" : agent ?? Theme.ClientName(Str(e, "cliente") ?? author) + " (sin nombre)";
            var chip = Theme.Chip(who, human ? Theme.Navy : Theme.ClientColor(Str(e, "cliente") ?? author));
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);

            var (text, color) = Describe(op, estado, ms);
            var doc = Str(e, "doc");
            var tb = new TextBlock { Text = text + (doc != null ? " · " + doc : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.B(color) };
            Grid.SetColumn(tb, 2);
            grid.Children.Add(tb);
            return grid;
        }

        private static (string, Color) Describe(string op, string estado, long ms)
        {
            var dur = ms > 0 ? " (" + FormatMs(ms) + ")" : "";
            if (estado == Status.RevitOcupado) return ("Revit estaba ocupado; el agente puede reintentar", Theme.Darken(Theme.Amber, 0.75));
            if (estado == Status.Desconocido) return (op + ": resultado DESCONOCIDO, requiere reconciliar" + dur, Theme.Darken(Theme.Red, 0.8));
            if (estado == Status.Rechazado && op != "commit") return (op + " rechazado (pausa, permisos o bloqueo)", Theme.Darken(Theme.Red, 0.8));
            if (estado == Status.Invalido) return (op + ": petición mal formada; se explicó cómo corregirla", Theme.Muted);
            if (estado == Status.Error) return (op + ": error; ver registro", Theme.Darken(Theme.Red, 0.8));
            switch (op)
            {
                case "conectar": return ("Se conectó", Theme.Ink);
                case "desconectar": return ("Se desconectó", Theme.Muted);
                case "context": return ("Revisó el estado de Revit" + dur, Theme.Ink);
                case "query": case "get": return ("Consulta" + dur, Theme.Ink);
                case "check": return ("Auditoría" + dur, Theme.Ink);
                case "sets": return ("Revisó sus conjuntos", Theme.Ink);
                case "plan": return ("Preparó un plan" + dur, Theme.Ink);
                case "job": return ("Consultó un trabajo o plan", Theme.Muted);
                case "exec": return ("C# dinámico " + estado + dur, Theme.Ink);
                case "commit":
                    if (estado == Status.Retenido) return ("Dejó un cambio en la cola", Theme.Darken(Theme.Amber, 0.75));
                    if (estado == Status.Ok) return ("Aplicó un cambio" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (estado == PlanStatus.Ensayado) return ("Ensayo: aplicado, verificado y revertido" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (estado == Status.Conflicto) return ("Cambio no aplicado: el modelo había cambiado", Theme.Darken(Theme.Amber, 0.75));
                    return ("Cambio no aplicado (" + estado + ")", Theme.Darken(Theme.Red, 0.8));
                case "aplicar_cola":
                    if (estado == PlanStatus.Aplicado) return ("Usted aplicó un plan de la cola" + dur, Theme.Darken(Theme.Green, 0.8));
                    if (estado == PlanStatus.Conflicto) return ("Un plan de la cola se omitió: el modelo había cambiado", Theme.Darken(Theme.Amber, 0.75));
                    return ("Plan de la cola: " + estado, Theme.Darken(Theme.Red, 0.8));
                case "retener": return (estado == "si" ? "Activó el modo Retener" : "Volvió al modo En vivo", Theme.Ink);
                case "pausar": return (estado == "si" ? "Pausó la IA" : "Reanudó la IA", Theme.Ink);
                case "descartar":
                case "descartar_cola": return ("Descartó cambios de la cola", Theme.Muted);
                default: return (op + " · " + estado + dur, Theme.Muted);
            }
        }

        /// <summary>Exporta la actividad reciente (JSONL tal cual y CSV) y una foto de las sesiones a %LOCALAPPDATA%\PDI.RevitMcp\exportes.</summary>
        private void Export()
        {
            try
            {
                var dir = Path.Combine(Home.Root, "exportes");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var recent = Runtime.Journal?.Recent(200) ?? new List<Dictionary<string, object>>();
                File.WriteAllLines(Path.Combine(dir, "actividad_" + stamp + ".jsonl"), recent.Select(e => Json.Write(e)));
                var csv = new StringBuilder("t,agente,cliente,op,estado,ms,doc,traza\n");
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
            catch (Exception ex) { MessageBox.Show("No se pudo exportar: " + ex.Message, Brand.Product); }
        }

        private static string Csv(string v) => v == null ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";

        private static void OpenFolder(string dir)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception) { }
        }
    }
}
