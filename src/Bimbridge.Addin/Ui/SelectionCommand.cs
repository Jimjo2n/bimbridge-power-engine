// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimbridge.Addin.Revit;
using Bimbridge.Engine;

namespace Bimbridge.Addin.Ui
{
    /// <summary>
    /// "Copy selection ID": puts a self-explanatory reference to the selected elements on the clipboard, to paste into any AI
    /// conversation. The reference ($sel-xxxxxx) resolves in every BIMBRIDGE command that takes a set, from any session.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CopySelectionCommand : IExternalCommand
    {
        internal static PushButton Button;
        private static DispatcherTimer _revert;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!Guard.Ready()) return Result.Cancelled;
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            var picked = uidoc?.Selection.GetElementIds();
            if (doc == null || picked == null || picked.Count == 0)
            {
                TaskDialog.Show("BIMBRIDGE", "Select one or more elements in the model first, then press Copy selection ID.");
                return Result.Cancelled;
            }

            var groups = picked.Select(id => doc.GetElement(id)).Where(e => e != null)
                .GroupBy(e => e.Category?.Name ?? "Other")
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => Tuple.Create(g.Key, g.Select(e => Ids.ToLong(e.Id)).OrderBy(x => x).ToList()))
                .ToList();
            var handle = DocRegistry.Instance.IdOf(doc);
            var reference = NamedSets.Pin(groups.SelectMany(g => g.Item2), handle, doc.Title);
            var text = NamedSets.DescribeSelection(reference, doc.Title, handle, groups);

            try { System.Windows.Clipboard.SetText(text); }
            catch (Exception ex)
            {
                // The clipboard can be held by another program for a moment; show the text so it can still be copied by hand.
                TaskDialog.Show("BIMBRIDGE", "Could not use the clipboard (" + ex.Message + "). Copy this text:\n\n" + text);
                return Result.Succeeded;
            }
            Flash(groups.Sum(g => g.Item2.Count));
            return Result.Succeeded;
        }

        /// <summary>Ribbon icon: selection frame (black corner brackets) around a grey element, on the 32×32 canvas of <see cref="Icons"/>.</summary>
        public static System.Windows.Media.ImageSource Icon(int size)
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.ScaleTransform(size / 32.0, size / 32.0));
                var pen = new System.Windows.Media.Pen(Theme.B(Theme.Action), 3) { StartLineCap = System.Windows.Media.PenLineCap.Square, EndLineCap = System.Windows.Media.PenLineCap.Square };
                foreach (var c in new[] { new System.Windows.Point(4, 4), new System.Windows.Point(28, 4), new System.Windows.Point(28, 28), new System.Windows.Point(4, 28) })
                {
                    var dx = c.X < 16 ? 7 : -7; var dy = c.Y < 16 ? 7 : -7;
                    dc.DrawLine(pen, c, new System.Windows.Point(c.X + dx, c.Y));
                    dc.DrawLine(pen, c, new System.Windows.Point(c.X, c.Y + dy));
                }
                // Not red: on the ribbon red means "needs attention" (paused, waiting).
                dc.DrawRoundedRectangle(Theme.B(Theme.Slate), null, new System.Windows.Rect(10, 10, 12, 12), 2, 2);
                dc.Pop();
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Brief confirmation on the button itself (no dialog: this is used often).</summary>
        private static void Flash(int count)
        {
            if (Button == null) return;
            try
            {
                Button.ItemText = "Copied\n" + count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " element" : " elements");
                _revert?.Stop();
                _revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _revert.Tick += (s, e) => { _revert.Stop(); try { Button.ItemText = "Copy\nselection ID"; } catch (Exception) { } };
                _revert.Start();
            }
            catch (Exception) { }
        }
    }
}
