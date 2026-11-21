// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using Bimbridge.Protocol;

namespace Bimbridge.Engine
{
    /// <summary>
    /// Switch stored as a file: if the file exists, it is on. Survives restarts
    /// and can be inspected or changed without opening Revit. Re-read at most once per second.
    ///   hold.on    → the AI's commits stay in the queue until "Apply queue".
    ///   paused.on  → the add-in rejects every AI request ("Pause AI").
    /// </summary>
    public class FlagFile
    {
        private readonly string _file;
        private readonly object _lock = new object();
        private bool _on;
        private DateTime _checkedUtc;
        /// <summary>0.9.0: the last command could not be saved to disk. Meanwhile memory wins: re-reading the disk would
        /// silently undo it a second later (this caused the flaky HeldCommit… test: "Hold" turned itself off).</summary>
        private bool _unsaved;

        public FlagFile(string home, string fileName)
        {
            _file = LegacyAliases.ResolveExisting(home, Path.Combine(home, fileName));   // a flag set by 0.9.3 keeps its legacy name until migrated
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
                catch (Exception) { _unsaved = true; /* the in-memory state wins even if the file fails (and it is not re-read) */ }
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

    /// <summary>Ribbon "Hold" button.</summary>
    public sealed class HoldSwitch : FlagFile
    {
        public HoldSwitch(string home) : base(home, "hold.on") { }
    }

    /// <summary>Ribbon "Pause AI" button.</summary>
    public sealed class PauseSwitch : FlagFile
    {
        public PauseSwitch(string home) : base(home, "paused.on") { }
    }
}
