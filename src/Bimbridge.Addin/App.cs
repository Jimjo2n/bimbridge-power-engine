// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit;
using Bimbridge.Addin.Ui;
using Bimbridge.Engine;
using Bimbridge.Protocol;
#if !REVIT2025_OR_GREATER
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace Bimbridge.Addin
{
    /// <summary>
    /// Entry point that Revit loads through the Bimbridge.addin manifest.
    /// Starts without prompts (section 5.1) and adds the "BIMBRIDGE" tab to the ribbon:
    /// Status, Pause AI, Live/Hold mode, Queue and Help.
    /// If anything fails at startup it is logged and Revit carries on normally: the MCP never stops Revit from opening.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        private Journal _journal;
        private MainThread _main;
        private DocumentTracker _tracker;
        private PipeHost _host;
        private InstanceDescriptor _descriptor;
        private string _logPath;
        private readonly object _logLock = new object();

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimbridge");
                Directory.CreateDirectory(home);
                _logPath = Path.Combine(home, "addin.log");

                var config = EngineConfig.Load(warn: Log);
                Runtime.Log = Log;
                try { UpdateService.Start(config); } catch (Exception ex) { Log("Updates: " + ex.Message); }
                try
                {
                    ZipUpdateCommand.KeysConfig = config?.UpdateKeys ?? new System.Collections.Generic.List<string>();
                    ZipUpdateCommand.Policy = config?.Profile ?? new Bimbridge.Protocol.ProfilePolicy();
                    var zipResult = ZipUpdateCommand.TakeResult();
                    if (zipResult != null) Log(zipResult);
                }
                catch (Exception ex) { Log("Update from ZIP: " + ex.Message); }
                if (!config.Enabled)
                {
                    Log("MCP disabled by configuration (enabled=false).");
                    return Result.Succeeded;
                }

                var revitVersion = app.ControlledApplication.VersionNumber;
                var addinAsm = Assembly.GetExecutingAssembly();
                var addinVersion = (Attribute.GetCustomAttribute(addinAsm, typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute)?.InformationalVersion
                    ?? addinAsm.GetName().Version.ToString();
                var pid = Process.GetCurrentProcess().Id;
                var pipeName = Wire.PipePrefix + Sanitize(Environment.UserName) + "." + pid;

                _journal = new Journal(Journal.DefaultDirectory, config.JournalDays);
                try { Ui.Runtime.Grants = new GrantStore(); }
                catch (Exception gex) when (gex is IOException || gex is UnauthorizedAccessException || gex is GrantStore.GrantKeyException) { Log("Grants unavailable: " + gex.Message + ". Tasks that write will not be authorized."); }
                try { Ui.Runtime.Audit = new AuditLog(AuditLog.DefaultPath); }
                catch (Exception aex) when (aex is IOException || aex is UnauthorizedAccessException) { Log("Durable audit unavailable: " + aex.Message + ". Code that writes will be rejected."); }
                _main = new MainThread(config.MainThreadBudgetMs, Log);
                _main.Create();
                _tracker = new DocumentTracker();
                _tracker.Attach(app.ControlledApplication);

                var plans = new PlanStore(PlanStore.DefaultQueueRoot);
                var hold = new HoldSwitch(home);
                var pause = new PauseSwitch(home);
                var sessions = new SessionRegistry();
                Runtime.Plans = plans;
                Runtime.Hold = hold;
                Runtime.Pause = pause;
                Runtime.Sessions = sessions;
                Runtime.RevitVersion = revitVersion;
                Runtime.Tracker = _tracker;
                Runtime.Journal = _journal;
                Runtime.Config = config;
                Runtime.BlockDynamicCode = config.BlockDynamicCode;
                Runtime.StartedUtc = DateTime.UtcNow;
                Runtime.Log = Log;
                CreateRibbon(app);
                app.Idling += OnIdling;
                app.ViewActivated += OnViewActivated;
                _tracker.DocumentHandleClosed += handle =>
                {
                    foreach (var s in sessions.All()) s.Sets.RemoveDoc(handle);
                    NamedSets.UnpinDoc(handle);
                };

                var handler = new RequestHandler(_main, _tracker, config, revitVersion, addinVersion, plans, hold, pause);
                Runtime.EngineDiagnostics = handler.Diagnostics;
                Runtime.Operations = handler.OperationsForPanel;
                Runtime.CancelOperation = handler.CancelFromPanel;
                _host = new PipeHost(pipeName, CreateSecurePipe, sessions, handler, config, _journal, Log);
                _host.Start();

                _descriptor = new InstanceDescriptor
                {
                    Pid = pid,
                    RevitVersion = revitVersion,
                    PipeName = pipeName,
                    Protocol = Wire.ProtocolVersion,
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    AddinVersion = addinVersion,
                };
                _descriptor.Write();
                Warmup.Start(Log);
                Log("Ready: Revit " + revitVersion + ", pipe " + pipeName + ", levels " + config.Levels + ", hold " + (hold.On ? "YES" : "NO"));
            }
            catch (Exception ex)
            {
                Log("Startup error (Revit carries on normally): " + ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            try
            {
                app.Idling -= OnIdling;
                app.ViewActivated -= OnViewActivated;
                UpdateService.Stop();
                _descriptor?.Delete();
                _host?.Dispose();
                if (_tracker != null) _tracker.Detach(app.ControlledApplication);
                _main?.Dispose();
                _journal?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Shutdown error: " + ex.Message);
            }
            return Result.Succeeded;
        }

        // 0.8.0: the tab is now called BIMBRIDGE (previously it had the legacy name).
        private const string TabName = "BIMBRIDGE";

        /// <summary>
        /// Own "BIMBRIDGE" tab: Connection (Status, Pause AI), AI changes (Mode, Queue) and Help (Settings, How it works, Version).
        /// Icons are drawn in code and refreshed on Idling according to the current state.
        /// </summary>
        private void CreateRibbon(UIControlledApplication app)
        {
            try
            {
                try { app.CreateRibbonTab(TabName); } catch (Autodesk.Revit.Exceptions.ArgumentException) { /* already exists */ }
                var dll = Assembly.GetExecutingAssembly().Location;
                var always = typeof(AlwaysAvailable).FullName;

                PushButtonData Btn(string id, string text, Type cmd, string tip, string longTip, System.Windows.Media.ImageSource large, System.Windows.Media.ImageSource small, bool anyTime = true)
                {
                    var d = new PushButtonData(id, text, dll, cmd.FullName)
                    {
                        ToolTip = tip,
                        LongDescription = longTip,
                        LargeImage = large,
                        Image = small,
                    };
                    if (anyTime) d.AvailabilityClassName = always;
                    return d;
                }

                var conn = app.CreateRibbonPanel(TabName, "Connection");
                Runtime.StatusButton = conn.AddItem(Btn("BimbridgeStatus", "Status\nno AI", typeof(StatusCommand),
                    "See which AIs are connected, what they can do and what they did.",
                    "Green: at least one AI is connected. Grey: waiting for Claude or Codex to connect. Red: the AI is paused.",
                    Icons.Status(Theme.Slate, 32), Icons.Status(Theme.Slate, 16))) as PushButton;
                Runtime.PauseButton = conn.AddItem(Btn("BimbridgePause", "Pause\nAI", typeof(PauseCommand),
                    "Stop the AI instantly. Press it again to resume.",
                    "While paused, Revit rejects every AI request (reads and changes). The AI is told that you paused it.",
                    Icons.Pause(false, 32), Icons.Pause(false, 16))) as PushButton;

                var selection = app.CreateRibbonPanel(TabName, "Selection");
                CopySelectionCommand.Button = selection.AddItem(Btn("BimbridgeCopySelection", "Copy\nselection ID", typeof(CopySelectionCommand),
                    "Copy a reference to the selected elements to paste into your AI conversation.",
                    "Select elements (for example 3 conduits and a receptacle), press this button and paste into Claude or Codex. " +
                    "The pasted text tells the AI which elements you mean ($sel-…), so you can say \"move these\" or \"check these\" without listing ids. " +
                    "Works in any conversation while this Revit is open.",
                    CopySelectionCommand.Icon(32), CopySelectionCommand.Icon(16), anyTime: false)) as PushButton;

                var changes = app.CreateRibbonPanel(TabName, "AI changes");
                Runtime.ModeButton = changes.AddItem(Btn("BimbridgeMode", "Mode\nLive", typeof(ModeCommand),
                    "Choose whether approved changes are applied immediately or wait in the queue.",
                    "Live (green): what you approve in the conversation is applied immediately.\nHold (amber): changes wait in the queue and the model is not touched until you apply them from Queue. Useful to keep working without interruptions.",
                    Icons.Mode(false, 32), Icons.Mode(false, 16))) as PushButton;
                Runtime.QueueButton = changes.AddItem(Btn("BimbridgeQueue", "Queue\nempty", typeof(QueueCommand),
                    "Review, apply or discard the changes that are waiting.",
                    "Shows each change with its before and after. If something changed while it was waiting, that plan is skipped and the AI is told. Each plan is undone with one Ctrl+Z. Does not save or sync.",
                    Icons.Queue(0, 32), Icons.Queue(0, 16), anyTime: false)) as PushButton;

                var sec = app.CreateRibbonPanel(TabName, "Authority");
                Runtime.AuthorizeButton = sec.AddItem(Btn("BimbridgeAuthorize", "Authorize\ntasks", typeof(AuthorizeCommand),
                    "Review what the AI's tasks ask to do and authorize it or not.",
                    "A task that modifies the model, saves, syncs or exports needs your permission for THAT task and THAT document, for 8 hours. " +
                    "The AI can ask for it but cannot grant it. You can also revoke active permissions here.",
                    Icons.Journal(32), Icons.Journal(16), anyTime: false)) as PushButton;

                var help = app.CreateRibbonPanel(TabName, "Help");
                help.AddItem(Btn("BimbridgeSettings", "Settings", typeof(SettingsCommand),
                    "Pause, change mode and dynamic C# in one place, with what each one means.",
                    "Each change applies immediately and shows on this ribbon. Allowing dynamic C# asks for confirmation. " +
                    "Permission levels (read, plan, apply) are shown here, but can only be changed with the installer.",
                    SettingsWindow.Icon(32), SettingsWindow.Icon(16)));
                help.AddItem(Btn("BimbridgeHelp", "How it\nworks", typeof(HelpCommand),
                    "Quick guide to the buttons.", "What each button does and what the AI can and cannot do.",
                    Icons.Help(32), Icons.Help(16)));
                UpdateService.Button = help.AddItem(Btn("BimbridgeUpdate", "Version\n" + Bimbridge.Setup.Updates.Format(UpdateService.Running), typeof(UpdateCommand),
                    "Find and install new versions without closing Revit.",
                    "Drop the new installer (.zip or .exe) in Downloads or on the Desktop: this button turns green and installs it with one click, " +
                    "with the same options. The new version takes effect when Revit restarts. You can also go back to the previous version.",
                    Icons.Update(0, 32), Icons.Update(0, 16))) as PushButton;

                // 0.8.0: developer tools. Only a person presses them.
                var dev = app.CreateRibbonPanel(TabName, "Developer Tools");
                dev.AddItem(Btn("BimbridgeZipUpdate", "Update\nfrom ZIP", typeof(ZipUpdateCommand),
                    "Install a verified BIMBRIDGE package (.zip). It is applied when Revit closes.",
                    "Opens a .zip package, checks its manifest, provenance signature, hashes and compatibility, shows the installed and the new version " +
                    "and asks for confirmation. A separate process installs it when you close Revit; BIMBRIDGE never closes or restarts Revit. " +
                    "If the installation cannot be verified, it goes back to the previous version.",
                    Icons.Update(1, 32), Icons.Update(1, 16)));
                Runtime.MarkDirty();
            }
            catch (Exception ex)
            {
                Log("Could not create the ribbon (the MCP keeps working): " + ex.Message);
            }
        }

        /// <summary>Which document is active, to set the target of each request without entering the Revit thread.</summary>
        private static void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try { DocRegistry.Instance.SetActive(e.Document); } catch (Exception) { }
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try { if (sender is UIApplication ui) Runtime.OnIdling(ui); }
            catch (Exception) { }
        }

        /// <summary>Pipe accessible only to the current Windows user. No network.</summary>
        private static NamedPipeServerStream CreateSecurePipe(string name)
        {
#if REVIT2025_OR_GREATER
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
            var security = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
        }

        private static string Sanitize(string s)
        {
            var chars = (s ?? "user").ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }

        private void Log(string message)
        {
            try
            {
                if (_logPath == null) return;
                // Several connections log at once: without a lock, one write collided and the line was lost.
                lock (_logLock) File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
