// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;

namespace PDI.RevitMcp.Engine
{
    /// <summary>
    /// Interruptor guardado como archivo: si el archivo existe, está encendido. Sobrevive a reinicios
    /// y se puede ver o cambiar sin abrir Revit. Se relee como mucho una vez por segundo.
    ///   retener.on  → los commits de la IA quedan en la cola hasta "Aplicar cola".
    ///   pausado.on  → el add-in rechaza toda petición de la IA ("Pausar IA").
    /// </summary>
    public class FlagFile
    {
        private readonly string _file;
        private readonly object _lock = new object();
        private bool _on;
        private DateTime _checkedUtc;
        /// <summary>0.9.0: la última orden no se pudo guardar en disco. Mientras tanto manda la memoria: releer el disco la
        /// desharía en silencio al segundo (era la causa de la prueba inestable HeldCommit…: "Retener" se apagaba solo).</summary>
        private bool _unsaved;

        public FlagFile(string home, string fileName)
        {
            _file = Path.Combine(home, fileName);
            Refresh();
        }

        public string Path_ => _file;

        public bool On
        {
            get
            {
                lock (_lock)
                {
                    if ((DateTime.UtcNow - _checkedUtc).TotalSeconds >= 1) Refresh();
                    return _on;
                }
            }
        }

        public bool Toggle() { var v = !On; Set(v); return v; }

        public void Set(bool on)
        {
            lock (_lock)
            {
                _on = on;
                _checkedUtc = DateTime.UtcNow;
                try
                {
                    if (on)
                    {
                        var dir = System.IO.Path.GetDirectoryName(_file);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(_file, DateTime.Now.ToString("o"));
                    }
                    else if (File.Exists(_file)) File.Delete(_file);
                    _unsaved = false;
                }
                catch (Exception) { _unsaved = true; /* el estado en memoria manda aunque el archivo falle (y no se relee) */ }
            }
        }

        public bool Unsaved { get { lock (_lock) return _unsaved; } }

        private void Refresh()
        {
            if (_unsaved) { _checkedUtc = DateTime.UtcNow; return; }
            try { _on = File.Exists(_file); } catch (Exception) { }
            _checkedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Botón "Retener" de la cinta.</summary>
    public sealed class HoldSwitch : FlagFile
    {
        public HoldSwitch(string home) : base(home, "retener.on") { }
    }

    /// <summary>Botón "Pausar IA" de la cinta.</summary>
    public sealed class PauseSwitch : FlagFile
    {
        public PauseSwitch(string home) : base(home, "pausado.on") { }
    }
}
