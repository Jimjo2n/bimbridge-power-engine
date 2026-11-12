// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using Autodesk.Revit.UI;
using PDI.RevitMcp.Addin.Revit;
using PDI.RevitMcp.Addin.Ui;
using PDI.RevitMcp.Engine;
using PDI.RevitMcp.Protocol;
#if !REVIT2025_OR_GREATER
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace PDI.RevitMcp.Addin
{
    /// <summary>
    /// Punto de entrada que Revit carga por el manifiesto PDI.RevitMcp.addin.
    /// Arranca sin preguntas (sección 5.1) y añade la pestaña "BIMBRIDGE" al ribbon:
    /// Estado, Pausar IA, Modo En vivo/Retener, Cola y Ayuda.
    /// Si algo falla al arrancar, se registra y Revit sigue normal: el MCP nunca impide abrir Revit.
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
                var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDI.RevitMcp");
                Directory.CreateDirectory(home);
                _logPath = Path.Combine(home, "addin.log");

                var config = EngineConfig.Load(warn: Log);
                Runtime.Log = Log;
                try { UpdateService.Start(config); } catch (Exception ex) { Log("Actualizaciones: " + ex.Message); }
                try
                {
                    ZipUpdateCommand.KeysConfig = config?.UpdateKeys ?? new System.Collections.Generic.List<string>();
                    ZipUpdateCommand.Policy = config?.Profile ?? new PDI.RevitMcp.Protocol.ProfilePolicy();
                    var zipResult = ZipUpdateCommand.TakeResult();
                    if (zipResult != null) Log(zipResult);
                }
                catch (Exception ex) { Log("Actualizar desde ZIP: " + ex.Message); }
                if (!config.Enabled)
                {
                    Log("MCP desactivado por configuración (enabled=false).");
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
                catch (Exception gex) when (gex is IOException || gex is UnauthorizedAccessException || gex is GrantStore.GrantKeyException) { Log("Concesiones no disponibles: " + gex.Message + ". Las tareas que escriben no se autorizarán."); }
                try { Ui.Runtime.Audit = new AuditLog(AuditLog.DefaultPath); }
                catch (Exception aex) when (aex is IOException || aex is UnauthorizedAccessException) { Log("Auditoría durable no disponible: " + aex.Message + ". Se rechazará el código que escribe."); }
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
                Log("Listo: Revit " + revitVersion + ", pipe " + pipeName + ", niveles " + config.Levels + ", retener " + (hold.On ? "SÍ" : "NO"));
            }
            catch (Exception ex)
            {
                Log("Error al arrancar (Revit sigue normal): " + ex);
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
                Log("Error al cerrar: " + ex.Message);
            }
            return Result.Succeeded;
        }

        // 0.8.0: la pestaña pasa a llamarse BIMBRIDGE (antes con el nombre heredado).
        private const string TabName = "BIMBRIDGE";

        /// <summary>
        /// Pestaña propia "BIMBRIDGE": Conexión (Estado, Pausar IA), Cambios de la IA (Modo, Cola) y Ayuda.
        /// Los iconos se dibujan en código y se actualizan en Idling según el estado.
        /// </summary>
        private void CreateRibbon(UIControlledApplication app)
        {
            try
            {
                try { app.CreateRibbonTab(TabName); } catch (Autodesk.Revit.Exceptions.ArgumentException) { /* ya existe */ }
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

                var conn = app.CreateRibbonPanel(TabName, "Conexión");
                Runtime.StatusButton = conn.AddItem(Btn("PdiMcpStatus", "Power Engine\nEstado", typeof(StatusCommand),
                    "Ver qué IAs están conectadas, qué pueden hacer y qué hicieron.",
                    "Verde: hay al menos una IA conectada. Gris: esperando a que Claude o Codex se conecten. Rojo: la IA está en pausa.",
                    Icons.Status(Theme.Slate, 32), Icons.Status(Theme.Slate, 16))) as PushButton;
                Runtime.PauseButton = conn.AddItem(Btn("PdiMcpPause", "Pausar\nIA", typeof(PauseCommand),
                    "Detener a la IA al instante. Púlselo otra vez para reanudar.",
                    "Mientras está en pausa, Revit rechaza toda petición de la IA (lecturas y cambios). La IA recibe el aviso de que usted la pausó.",
                    Icons.Pause(false, 32), Icons.Pause(false, 16))) as PushButton;

                var changes = app.CreateRibbonPanel(TabName, "Cambios de la IA");
                Runtime.ModeButton = changes.AddItem(Btn("PdiMcpMode", "Modo\nEn vivo", typeof(ModeCommand),
                    "Elegir si los cambios aprobados se aplican al momento o esperan en la cola.",
                    "En vivo (verde): lo que usted apruebe en la conversación se aplica al momento.\nRetener (ámbar): los cambios esperan en la cola y el modelo no se toca hasta que usted los aplique desde Cola. Útil para seguir trabajando sin interrupciones.",
                    Icons.Mode(false, 32), Icons.Mode(false, 16))) as PushButton;
                Runtime.QueueButton = changes.AddItem(Btn("PdiMcpQueue", "Cola\nvacía", typeof(QueueCommand),
                    "Revisar, aplicar o descartar los cambios que esperan.",
                    "Muestra cada cambio con su antes y después. Si algo cambió mientras esperaba, ese plan se omite y la IA se entera. Cada plan se deshace con un Ctrl+Z. No guarda ni sincroniza.",
                    Icons.Queue(0, 32), Icons.Queue(0, 16), anyTime: false)) as PushButton;

                var sec = app.CreateRibbonPanel(TabName, "Autoridad");
                Runtime.AuthorizeButton = sec.AddItem(Btn("PdiMcpAuthorize", "Autorizar\ntareas", typeof(AuthorizeCommand),
                    "Revisar lo que las tareas de la IA piden hacer y autorizarlo o no.",
                    "Una tarea que modifica el modelo, guarda, sincroniza o exporta necesita su permiso para ESA tarea y ESE documento, durante 8 horas. " +
                    "La IA puede pedirlo pero no concederlo. También permite revocar permisos vigentes.",
                    Icons.Status(Theme.Slate, 32), Icons.Status(Theme.Slate, 16), anyTime: false)) as PushButton;
                Runtime.TrustButton = sec.AddItem(Btn("PdiMcpTrust", "Bloquear\ninyección C#", typeof(TrustCommand),
                    "Bloquear o permitir que la IA ejecute código C# dinámico (exec).",
                    "Por defecto la IA local autorizada puede ejecutar C# dinámico, auditado. Pulse para bloquearlo: toda petición nueva de exec se rechaza. " +
                    "El cambio queda guardado en config.json. No detiene lo que ya está dentro de Revit. Las operaciones tipadas (plan/commit) siguen disponibles.",
                    Icons.Mode(false, 32), Icons.Mode(false, 16))) as PushButton;

                var help = app.CreateRibbonPanel(TabName, "Ayuda");
                help.AddItem(Btn("PdiMcpHelp", "Cómo\nfunciona", typeof(HelpCommand),
                    "Guía rápida de los botones.", "Qué hace cada botón y qué puede y no puede hacer la IA.",
                    Icons.Help(32), Icons.Help(16)));
                UpdateService.Button = help.AddItem(Btn("PdiMcpUpdate", "Versión\n" + PDI.RevitMcp.Setup.Updates.Format(UpdateService.Running), typeof(UpdateCommand),
                    "Buscar e instalar versiones nuevas sin cerrar Revit.",
                    "Deje el instalador nuevo (.zip o .exe) en Descargas o en el Escritorio: este botón se pone verde y lo instala con un clic, " +
                    "con las mismas opciones. La versión nueva entra al reiniciar Revit. También permite volver a la versión anterior.",
                    Icons.Update(0, 32), Icons.Update(0, 16))) as PushButton;

                // 0.8.0: herramientas de desarrollo. Sólo las pulsa una persona.
                var dev = app.CreateRibbonPanel(TabName, "Developer Tools");
                dev.AddItem(Btn("BimbridgeZipUpdate", "Actualizar\ndesde ZIP", typeof(ZipUpdateCommand),
                    "Instalar un paquete de BIMBRIDGE (.zip) verificado. Se aplica al cerrar Revit.",
                    "Abre un paquete .zip, comprueba su manifiesto, firma de procedencia, hashes y compatibilidad, muestra la versión instalada y la nueva " +
                    "y pide confirmación. La instalación la hace un proceso aparte cuando usted cierra Revit; BIMBRIDGE nunca cierra ni reinicia Revit. " +
                    "Si la instalación no queda verificada, vuelve a la versión anterior.",
                    Icons.Update(1, 32), Icons.Update(1, 16)));
                Runtime.MarkDirty();
            }
            catch (Exception ex)
            {
                Log("No se pudo crear el ribbon (el MCP sigue funcionando): " + ex.Message);
            }
        }

        /// <summary>Cuál documento está activo, para fijar el destino de cada petición sin entrar al hilo de Revit.</summary>
        private static void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try { DocRegistry.Instance.SetActive(e.Document); } catch (Exception) { }
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try { if (sender is UIApplication ui) Runtime.OnIdling(ui); }
            catch (Exception) { }
        }

        /// <summary>Pipe sólo accesible por el usuario de Windows actual. Sin red.</summary>
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
            var chars = (s ?? "usuario").ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }

        private void Log(string message)
        {
            try
            {
                if (_logPath == null) return;
                // Varias conexiones registran a la vez: sin candado, una escritura chocaba y se perdía la línea.
                lock (_logLock) File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
