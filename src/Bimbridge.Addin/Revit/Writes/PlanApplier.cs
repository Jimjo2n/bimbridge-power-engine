// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Bimbridge.Engine;
using Bimbridge.Protocol;

namespace Bimbridge.Addin.Revit.Writes
{
    /// <summary>
    /// Apply an already approved plan. Runs on the Revit thread (the commit's ExternalEvent or the Apply queue button).
    ///
    ///  1. Re-reads each "before": if anything changed since planning (another AI, the user), it touches nothing → conflict.
    ///     Creations have guards (the source still exists, the element is not tagged yet, the sheet number is still free…).
    ///  2. Applies in plan order, one transaction per operation (or per sub-batch of N changes), all inside ONE assimilated
    ///     TransactionGroup: a single Undo entry. Any Revit error rolls back the whole plan.
    ///  3. @n: what operation n creates is recorded and later operations use it (ids=@n, view=@n, hoja=@n…).
    ///  4. Identical moves, deletes and hides are grouped into a single API call (MoveElements, Delete, HideElements).
    ///  5. When done, re-reads what it touched and compares it with the plan ("verificacion").
    /// </summary>
    internal sealed partial class PlanApplier
    {
        private readonly Document _doc;
        private readonly Plan _plan;
        private readonly int _subBatch;
        private readonly string _label;
        private readonly PlanOps.ApplyResult _r = new PlanOps.ApplyResult();
        private readonly Dictionary<long, TagKit> _tagKits = new Dictionary<long, TagKit>();
        private readonly HashSet<long> _deleted = new HashSet<long>();
        private readonly Dictionary<string, List<long>> _skipped = new Dictionary<string, List<long>>();
        private readonly HashSet<string> _notes = new HashSet<string>();
        /// <summary>alpha3: instead of assimilating, verify inside the group and roll it back (commit modo=ensayo).</summary>
        public bool Trial;

        public PlanApplier(Document doc, Plan plan, int subBatch, string label)
        {
            _doc = doc;
            _plan = plan;
            _subBatch = Math.Max(1, subBatch);
            _label = label;
        }

        // =====================================================================================================
        // Current value (for conflicts and verification)
        // =====================================================================================================

        private static bool Special(string key) => key == "elev" || key == "ws" || key == "type";

        /// <summary>Current canonical value of (element, field), in the same shape as the "before" that PlanBuilder saved.</summary>
        public string Current(PlanEntry entry)
        {
            if (entry.Ref > 0) return "";                    // template over what the plan will create: there is no "before"
            if (entry.Key == "nuevo") return Guard(entry);   // creation: guards
            if (entry.Key != null && entry.Key.Contains("@@")) return ""; // depends on a view the plan creates
            var e = _doc.GetElement(Ids.ToId(entry.Id));
            if (entry.Key == "exists") return e == null ? "0" : "1";
            if (e == null) return WriteKit.Missing;
            if (entry.Op == "set_param" || entry.Op == "set_type_param" || (entry.Op == "set_values" && !Special(entry.Key)))
                return WriteKit.ParamValue(e.LookupParameter(entry.Key));
            var v = e as View;
            switch (entry.Key)
            {
                case "type": return WriteKit.TypeName(_doc, e);
                case "elev": return PlanEntry.Canon(WriteKit.ElevParam(e)?.AsDouble());
                case "ws": return WriteKit.WorksetName(_doc, e);
                case "lvl": return WriteKit.LevelOf(_doc, e)?.Name ?? "";
                case "loc": return WriteKit.Loc(e);
                case "rot": return WriteKit.Loc(e) + "|" + WriteKit.Rot(e);
                case "head": return e is IndependentTag tag ? PlanBuilder.HeadCanon(tag) : WriteKit.Missing;
                case "llamada_luminaria":
                    if (!(e is TextNote note) || LightingCallout.Read(note) is not Dictionary<string, object> link) return WriteKit.Missing;
                    try
                    {
                        var state = LightingCallout.Observe(_doc, link);
                        return LightingCallout.NoteFingerprint(note, link, LightingCallout.Fingerprint(link, state.Text, state.Point, state.TypeUid));
                    }
                    catch (QueryException ex) { return "\u0000" + ex.Message; }
                case "plantilla": return v == null ? WriteKit.Missing : (_doc.GetElement(v.ViewTemplateId) as View)?.Name ?? "ninguna";
                case "rango": return v is ViewPlan vp ? PlanBuilder.RangeCanon(_doc, vp) : "";
                case "escala": return v == null ? WriteKit.Missing : PlanEntry.Canon((long)v.Scale);
                case "detalle": return v == null ? WriteKit.Missing : PlanBuilder.DetailName(v.DetailLevel);
                case "nombre": return e.Name;
                case "recorte": return v == null ? WriteKit.Missing : (v.CropBoxActive ? "1" : "0");
                case "scope_box":
                {
                    var sp = e.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    return sp == null ? WriteKit.Missing : _doc.GetElement(sp.AsElementId())?.Name ?? "ninguno";
                }
                case "caja_seccion": return v is View3D v3 ? PlanBuilder.SectionBoxCanon(v3) : WriteKit.Missing;
            }
            if (entry.Key.StartsWith("oculto@", StringComparison.Ordinal))
            {
                var view = ViewOf(WriteKit.Dict(entry.Target));
                return view == null ? WriteKit.Missing : (e.IsHidden(view) ? "1" : "0");
            }
            if (entry.Key.StartsWith("graficos@", StringComparison.Ordinal))
            {
                var view = ViewOf(WriteKit.Dict(entry.Target));
                return view == null ? WriteKit.Missing : WriteKit.Overrides(view.GetElementOverrides(e.Id));
            }
            if (entry.Key.StartsWith("cat_oculta:", StringComparison.Ordinal))
                return v == null ? WriteKit.Missing : (v.GetCategoryHidden(Ids.ToId(WriteKit.L(WriteKit.Dict(entry.Target), "cat"))) ? "1" : "0");
            if (entry.Key.StartsWith("cat_graficos:", StringComparison.Ordinal))
                return v == null ? WriteKit.Missing : WriteKit.Overrides(v.GetCategoryOverrides(Ids.ToId(WriteKit.L(WriteKit.Dict(entry.Target), "cat"))));
            // Old plans (0.2.x): the field is a parameter.
            return WriteKit.ParamValue(e.LookupParameter(entry.Key));
        }

        /// <summary>"" if the creation is still possible; otherwise the reason (prefixed with \0, like the other technical values).</summary>
        private string Guard(PlanEntry entry)
        {
            var t = WriteKit.Dict(entry.Target);
            bool Exists(string k) => !t.ContainsKey(k) || _doc.GetElement(Ids.ToId(WriteKit.L(t, k))) != null;
            switch (entry.Op)
            {
                case "copy":
                case "mirror":
                    return Exists("src") ? "" : "\u0000the original no longer exists";
                case "place":
                    return Exists("sym") && Exists("lvl") ? "" : "\u0000the type or the level no longer exists";
                case "tag":
                {
                    if (!Exists("elem")) return "\u0000the element no longer exists";
                    if (!t.ContainsKey("view")) return "";
                    var view = _doc.GetElement(Ids.ToId(WriteKit.L(t, "view"))) as View;
                    if (view == null) return "\u0000the view no longer exists";
                    return Kit(view).Tagged.Contains(WriteKit.L(t, "elem")) ? "\u0000already tagged in that view (someone tagged it after planning)" : "";
                }
                case "group_create":
                    foreach (var id in WriteKit.Longs(t, "ids"))
                    {
                        var e = _doc.GetElement(Ids.ToId(id));
                        if (e == null) return "\u0000" + id + " no longer exists";
                        if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) return "\u0000" + id + " is already in a group";
                    }
                    return "";
                case "group_place":
                    if (!Exists("gtype") || !Exists("nivel") || !Exists("prototipo")) return "\u0000the group type, level or prototype no longer exists";
                    if (t.ContainsKey("prototipo") && GroupPlacement.Signature((Group)_doc.GetElement(Ids.ToId(WriteKit.L(t, "prototipo")))) != WriteKit.S(t, "firma_miembros"))
                        return "\u0000the prototype's members changed";
                    return "";
                case "group_variant":
                    try { GroupVariant.Validate(_doc, t); return ""; }
                    catch (QueryException ex) { return "\u0000" + ex.Message; }
                case "view_create":
                {
                    if (!Exists("nivel") || !Exists("vft") || !Exists("origen") || (t.ContainsKey("plantilla") && !Exists("plantilla"))) return "\u0000the level, type, template or source no longer exists";
                    var name = WriteKit.S(t, "nombre");
                    var vtype = ModelIndex.ViewTypeFor(WriteKit.S(t, "tipo"));
                    if (name != null && vtype != null && new ModelIndex(_doc).ViewNameTaken(name, vtype))
                        return "\u0000a " + vtype + " view named '" + name + "' already exists";
                    return "";
                }
                case "sheet_create":
                {
                    if (!Exists("tb")) return "\u0000the titleblock no longer exists";
                    var number = WriteKit.S(t, "numero");
                    if (new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(x => string.Equals(x.SheetNumber, number, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000sheet " + number + " already exists";
                    return "";
                }
                case "sheet_set":
                {
                    if (!Exists("reemplaza")) return "\u0000the set to be replaced no longer exists";
                    if (((List<object>)t["hojas"]).Any(h => _doc.GetElement(Ids.ToId(Convert.ToInt64(h, CultureInfo.InvariantCulture))) == null)) return "\u0000a sheet no longer exists";
                    var nm = WriteKit.S(t, "nombre");
                    if (!t.ContainsKey("reemplaza") && new FilteredElementCollector(_doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().Any(s => string.Equals(s.Name, nm, StringComparison.OrdinalIgnoreCase)))
                        return "\u0000the set '" + nm + "' already exists";
                    return "";
                }
                case "sheet_place":
                {
                    if (!Exists("hoja") || !Exists("vista")) return "\u0000the sheet or the view no longer exists";
                    if (t.ContainsKey("hoja") && t.ContainsKey("vista") &&
                        !Viewport.CanAddViewToSheet(_doc, Ids.ToId(WriteKit.L(t, "hoja")), Ids.ToId(WriteKit.L(t, "vista"))))
                        return "\u0000Revit no longer allows placing that view on that sheet (did someone place it on another one?)";
                    return "";
                }
                case "text_note":
                    if (t.TryGetValue("llamada", out var data) && data is Dictionary<string, object> config)
                    {
                        try
                        {
                            var state = LightingCallout.Observe(_doc, config);
                            if (LightingCallout.Fingerprint(config, state.Text, state.Point, state.TypeUid) != WriteKit.S(t, "huella_origen"))
                                return "\u0000the source fixture changed";
                        }
                        catch (QueryException ex) { return "\u0000" + ex.Message; }
                    }
                    return Exists("view") && Exists("ttype") ? "" : "\u0000the view or the type no longer exists";
                case "filled_region":
                    return Exists("view") && Exists("ttype") && Exists("frtype") ? "" : "\u0000the view or the type no longer exists";
                case "annotation_rebind":
                    try { ReadAnnotationTargets(t); return ""; }
                    catch (QueryException ex) { return "\u0000" + ex.Message; }
                case "detail_line": case "detail_rect": case "detail_polygon": case "detail_arc":
                    return Exists("view") && Exists("gs") ? "" : "\u0000the view or the line style no longer exists";
                case "detail_component":
                    return Exists("view") && Exists("sym") ? "" : "\u0000the view or the component no longer exists";
                case "dimension":
                {
                    if (!Exists("view") || !Exists("dtype")) return "\u0000the view or the dimension type no longer exists";
                    foreach (var r in (List<object>)t["refs"]) if (_doc.GetElement(Ids.ToId(Convert.ToInt64(r, CultureInfo.InvariantCulture))) == null) return "\u0000line " + r + " no longer exists";
                    return "";
                }
                default:
                    return ModelGuard(entry.Op, t) ?? "";
            }
        }

        private TagKit Kit(View view)
        {
            var key = Ids.ToLong(view.Id);
            if (!_tagKits.TryGetValue(key, out var kit)) _tagKits[key] = kit = TagKit.For(_doc, view);
            return kit;
        }

        /// <summary>View of an entry: "view" (id) or "view_ref" (the one created by op n of the plan).</summary>
        private View ViewOf(Dictionary<string, object> t, string idKey = "view", string refKey = "view_ref")
        {
            if (t.ContainsKey(idKey)) return _doc.GetElement(Ids.ToId(WriteKit.L(t, idKey))) as View;
            if (t.ContainsKey(refKey)) return _doc.GetElement(Ids.ToId(FirstCreated((int)WriteKit.L(t, refKey), refKey))) as View;
            return null;
        }

        private long FirstCreated(int op, string what)
        {
            if (_plan == null || !_plan.CreatedByOp.TryGetValue(op, out var l) || l.Count == 0)
                throw new InvalidOperationException(what + " @" + op + ": that operation created nothing");
            return l[0];
        }

        // =====================================================================================================
        // Apply
        // =====================================================================================================

        public PlanOps.ApplyResult Run()
        {
            var r = _r;
            var conflicts = _plan.FindConflicts(Current);
            if (conflicts.Count > 0)
            {
                r.Status = PlanStatus.Conflicto;
                r.Detail = conflicts.Count + " changes no longer match the plan (e.g. " +
                           string.Join("; ", conflicts.Take(3).Select(Describe)) + "). Nothing was applied; plan again.";
                return r;
            }
            if (_doc.IsReadOnly)
            {
                r.Status = PlanStatus.Revertido;
                r.Detail = "the document is read-only";
                return r;
            }

            var batches = Batches();
            _plan.TotalBatches = batches.Count;
            _plan.AppliedBatches = 0;
            _plan.Reconciliation = null;
            _plan.Created.Clear();
            _plan.CreatedByOp.Clear();

            var summary = string.IsNullOrWhiteSpace(_plan.Summary) ? "" : " · " + (_plan.Summary.Length > 48 ? _plan.Summary.Substring(0, 48) + "…" : _plan.Summary);
            var groupName = DocumentTracker.TransactionPrefix + " · " + _label + " · " + _plan.Id + summary;
            var group = new TransactionGroup(_doc, groupName);
            PlanOps.ApplyResult outcome = null;
            try { outcome = RunGroup(group, batches); }
            catch (Exception ex)
            {
                // Only what RunGroup could not handle gets here (e.g. a failure composing the receipt): nothing can be asserted.
                outcome = AbortGroup(group, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { group.Dispose(); }
                catch (Exception ex)
                {
                    // Dispose must not erase the result or the evidence.
                    var message = "Error while disposing the transaction group: " + ex.GetType().Name + ": " + ex.Message;
                    if (outcome == null) { GroupAbort.PreserveInterrupted(_plan, message); }
                    else
                    {
                        var status = GroupAbort.StatusAfterDisposeError(outcome.Status);
                        if (status != outcome.Status) { outcome.Status = status; GroupAbort.Preserve(_plan, GroupAbort.NotObserved, message, Evidence()); }
                        outcome.Warnings.Add(message);
                    }
                }
            }
            return outcome;
        }

        private PlanOps.ApplyResult RunGroup(TransactionGroup group, List<List<PlanEntry>> batches)
        {
            var r = _r;
            {
                if (group.Start() != TransactionStatus.Started)
                {
                    r.Status = PlanStatus.Revertido;
                    r.Detail = "Revit did not allow the transaction to start (is another command running?)";
                    return r;
                }
                try
                {
                    for (var k = 0; k < batches.Count; k++)
                    {
                        var failures = new FailureLog();
                        using (var t = new Transaction(_doc, DocumentTracker.TransactionPrefix + " · " + _plan.Id + " " + (k + 1) + "/" + batches.Count))
                        {
                            var opts = t.GetFailureHandlingOptions();
                            opts.SetFailuresPreprocessor(failures);
                            opts.SetClearAfterRollback(true);
                            t.SetFailureHandlingOptions(opts);
                            if (t.Start() != TransactionStatus.Started)
                                return AbortGroup(group, "Revit did not start batch " + (k + 1));
                            PlanEntry current = null;
                            try
                            {
                                ApplyBatch(batches[k], e => current = e);
                            }
                            catch (Exception ex)
                            {
                                t.RollBack();
                                return AbortGroup(group, "failure in " + Where(current) + ": " + ex.Message);
                            }
                            var st = t.Commit();
                            if (st != TransactionStatus.Committed)
                            {
                                return AbortGroup(group, "Revit rejected step " + (k + 1) + "/" + batches.Count + " (" + OpCatalog.NameOf(batches[k][0].Op) + ")" +
                                    (failures.Errors.Count > 0 ? ": " + string.Join(" | ", failures.Errors.Distinct().Take(3)) : ""));
                            }
                            r.Warnings.AddRange(failures.Warnings);
                            _plan.AppliedBatches = k + 1;
                        }
                        // Created ids must be on disk before the next batch; without that, a crash could not be reconciled.
                        try { Ui.Runtime.Plans?.Checkpoint(_plan); }
                        catch (PlanStoreException ex) { return AbortGroup(group, ex.Message); }
                    }
                    var verificationError = VerifySourcePlacements() ?? VerifyConduitRoutes();
                    if (verificationError != null) return AbortGroup(group, verificationError);
                    foreach (var callout in _plan.Entries.Where(e => e.Key == "llamada_luminaria"))
                        if (!PlanEntry.Same(Current(callout), callout.After))
                            return AbortGroup(group, "Callout " + callout.Id + " did not keep its text, position or link after regenerating");
                    foreach (var (target, id) in _managedGroups)
                        if (GroupPlacement.Difference(_doc.GetElement(id) as Group, target) is string difference)
                            return AbortGroup(group, "Group " + Ids.ToLong(id) + " differs in " + difference);
                    foreach (var annotation in _replacementAnnotations)
                        if (!ReplacementAnnotationIsValid(annotation))
                            return AbortGroup(group, "The new annotation " + Ids.ToLong(annotation.NewId) + " lost the expected references");
                    foreach (var (target, id) in _groupVariants)
                        if (_doc.GetElement(Ids.ToId(WriteKit.L(target, "grupo"))) != null ||
                            !GroupVariant.Matches(_doc.GetElement(id) as Group, target))
                            return AbortGroup(group, "The group variant lost its members or its placement");
                    if (Trial) return FinishTrial(group);
                    if (group.Assimilate() != TransactionStatus.Committed)
                        return AbortGroup(group, "Revit did not confirm assimilating the group");
                }
                catch (Exception ex)
                {
                    return AbortGroup(group, ex.GetType().Name + ": " + ex.Message);
                }
            }

            r.Status = PlanStatus.Aplicado;
            r.Created = _plan.Created.Count;
            _plan.Verification = Verify();
            foreach (var kv in _skipped)
                r.Skipped.Add(kv.Value.Count + " not applied because " + kv.Key + " (" + string.Join(",", kv.Value.Take(6)) + (kv.Value.Count > 6 ? ",…" : "") + ")");
            r.Detail = r.Changed + " changes" + (r.Created > 0 ? ", " + r.Created + " new elements" : "") +
                       (r.Deleted > 0 ? ", " + r.Deleted + " elements deleted (including dependents)" : "") +
                       (r.Skipped.Count > 0 ? "; " + string.Join("; ", r.Skipped.Take(3)) : "") +
                       (_notes.Count > 0 ? "; " + string.Join("; ", _notes.Take(3)) : "") +
                       (r.Warnings.Count > 0 ? "; Revit warnings (" + string.Join(", ", r.Warnings.GroupBy(w => w.Split(':')[0]).Select(g => g.Key + " ×" + g.Count())) +
                        "): " + string.Join(" | ", r.Warnings.Distinct().Take(3)) : "");
            return r;
        }

        private PlanOps.ApplyResult AbortGroup(TransactionGroup group, string detail)
        {
            var state = RollBack(group, out var error);
            var rolledBack = state == GroupAbort.RolledBack;
            if (rolledBack) ResetCreated();
            else GroupAbort.Preserve(_plan, state, error, Evidence());
            _r.Created = _plan.Created.Count;
            _r.Status = rolledBack ? PlanStatus.Revertido : Status.Desconocido;
            _r.Detail = detail + (rolledBack ? ". Revit confirmed the group rollback." : ". The group rollback was not confirmed.");
            return _r;
        }

        private static string RollBack(TransactionGroup group, out string error) =>
            GroupAbort.Attempt(group.HasStarted, () => group.RollBack().ToString(), () => group.GetStatus().ToString(), out error);

        private GroupAbort.Evidence Evidence() => new GroupAbort.Evidence
        {
            Deleted = _deleted, Changed = _r.Changed, DeletedCount = _r.Deleted, Skipped = _skipped, Warnings = _r.Warnings,
        };

        private PlanOps.ApplyResult FinishTrial(TransactionGroup group)
        {
            var r = _r;
            var created = _plan.Created.ToList();
            var changed = r.Changed;
            var state = GroupAbort.NotObserved;
            string rollbackError = null;
            var during = TrialGuard.VerifyAndRollback(VerifyTrial, () => { state = RollBack(group, out rollbackError); });
            var functional = Arg.GetBool(during, "ok") == true;
            int checkedCount = 0, restored = 0;
            var diffs = new List<object>();
            var leftovers = new List<object>();
            string readError = null;
            if (state == GroupAbort.RolledBack)
            {
                try
                {
                    foreach (var e in _plan.Entries)
                    {
                        if (e.Ref > 0 || e.Id == 0 || e.Key == "nuevo" || (e.Key != null && e.Key.Contains("@@"))) continue;
                        var now = Current(e);
                        checkedCount++;
                        if (PlanEntry.Same(now, e.Before)) { restored++; continue; }
                        if (diffs.Count < 5) diffs.Add(e.Id + " " + e.Key + ": before " + Plan.ShowValue(e, e.Before, true) + ", now " + Plan.ShowValue(e, now, false));
                    }
                    foreach (var id in created)
                        if (_doc.GetElement(Ids.ToId(id)) != null) leftovers.Add(id);
                    foreach (var id in _deleted.Where(id => !created.Contains(id)))
                        if (_doc.GetElement(Ids.ToId(id)) == null) diffs.Add("deletion not restored: " + id);
                }
                catch (Exception ex) { readError = ex.GetType().Name + ": " + ex.Message; }
            }
            var rollbackConfirmed = state == GroupAbort.RolledBack;
            var restoration = rollbackConfirmed && readError == null && restored == checkedCount && diffs.Count == 0 && leftovers.Count == 0;
            r.Status = TrialGuard.TrialStatus(functional, restoration);
            r.Trial = new Dictionary<string, object>
            {
                ["verificacion_temporal"] = during,
                ["clasificacion"] = TrialGuard.Classify(functional, rollbackConfirmed, restoration),
                ["temporal_ok"] = functional,
                ["grupo_estado"] = state,
                ["rollback_confirmado"] = rollbackConfirmed,
                ["rollback_error"] = rollbackError,
                ["relectura_error"] = readError,
                ["releidos_tras_revertir"] = (long)checkedCount,
                ["restaurados"] = (long)restored,
                ["creados_temporales"] = (long)created.Count,
                ["creados_que_persisten"] = rollbackConfirmed && readError == null ? leftovers : null,
                ["creados_sin_observar"] = rollbackConfirmed && readError == null ? null : created.Cast<object>().ToList(),
                ["restauracion_ok"] = restoration,
            };
            if (diffs.Count > 0) r.Trial["no_restaurados"] = diffs;
            if (restoration) ResetCreated();
            else GroupAbort.Preserve(_plan, state, rollbackError ?? readError, Evidence());
            r.Detail = !restoration ? "Trial uncertain. Review the reconciliation evidence before repeating."
                : functional ? "Trial verified and rolled back. Temporary changes: " + changed + ", created: " + created.Count + "."
                : "The trial's functional verification failed. The rollback and the restoration are confirmed.";
            return r;
        }

        private Dictionary<string, object> VerifyTrial()
        {
            try
            {
                var result = Verify();
                var surviving = _plan.Created.Where(id => !_deleted.Contains(id)).ToList();
                var verifiedCreations = surviving.Count(id => _doc.GetElement(Ids.ToId(id)) != null);
                var expectedCreations = GroupAbort.ExpectedSurvivingCreations(_plan.CreationCount, _plan.Created, _deleted);
                var missingCreations = _plan.Entries.Where(e => e.IsCreation).GroupBy(e => e.OpIndex)
                    .Where(g => !_plan.CreatedByOp.TryGetValue(g.Key, out var ids) || ids.Count < g.Count())
                    .Select(g => (object)(long)g.Key).ToList();
                var expectedDeletes = _plan.Entries.Where(e => e.Ref == 0 && e.Id != 0 && e.Key == "exists" && e.After == "0")
                    .Select(e => e.Id).Concat(_deleted).Distinct();
                var missingDeletes = expectedDeletes.Where(id => _doc.GetElement(Ids.ToId(id)) != null).Cast<object>().ToList();
                result["creaciones_esperadas"] = (long)expectedCreations;
                result["creaciones_verificadas"] = (long)verifiedCreations;
                result["ops_sin_creacion_esperada"] = missingCreations;
                result["borrados_que_persisten"] = missingDeletes;
                result["ok"] = TrialGuard.FunctionalSuccess((int)(Arg.GetLong(result, "releidos") ?? 0),
                    PlanVerification.Postconditions(result) != PlanVerification.Met || missingDeletes.Count > 0, _skipped.Count > 0,
                    expectedCreations, verifiedCreations, missingCreations.Count > 0 || verifiedCreations != surviving.Count);
                return result;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
        }

        private void ResetCreated()
        {
            _plan.Created.Clear();
            _plan.CreatedByOp.Clear();
            _r.Changed = 0; _r.Deleted = 0;
        }

        private string Describe(PlanEntry c)
        {
            string now;
            try { now = Current(c); } catch (Exception ex) { now = "\u0000" + ex.Message; }
            var shown = now.StartsWith("\u0000", StringComparison.Ordinal) ? now.Substring(1) : now;
            if (c.Key == "nuevo") return OpCatalog.NameOf(c.Op) + ": " + shown;
            return c.Id + " " + c.Key + ": expected '" + Plan.ShowValue(c, c.Before, true) + "', found '" + Plan.ShowValue(c, shown, true) + "'";
        }

        private static string Where(PlanEntry e)
        {
            if (e == null) return "the plan";
            var who = e.Ref > 0 ? "what was created by @" + e.Ref : e.Id == 0 ? "a creation" : e.Id.ToString(CultureInfo.InvariantCulture);
            return OpCatalog.NameOf(e.Op) + " (op " + e.OpIndex + ") on " + who + (e.Key != null && e.Key != "nuevo" ? " [" + e.Key + "]" : "");
        }

        /// <summary>
        /// Steps in plan order: each operation in its own transaction (what it creates is regenerated for the next one) and,
        /// within an operation, sub-batches of N changes. Old plans without an operation number: changes first, deletes last.
        /// </summary>
        private List<List<PlanEntry>> Batches()
        {
            IEnumerable<List<PlanEntry>> groups;
            if (_plan.Entries.All(e => e.OpIndex == 0))
            {
                var g = new List<List<PlanEntry>>
                {
                    _plan.Entries.Where(e => e.Key != "exists").ToList(),
                    _plan.Entries.Where(e => e.Key == "exists").ToList(),
                };
                groups = g.Where(x => x.Count > 0);
            }
            else
            {
                groups = _plan.Entries.Select((e, i) => new { e, i }).GroupBy(x => x.e.OpIndex).OrderBy(x => x.Key)
                    .Select(x => x.OrderBy(y => y.i).Select(y => y.e).ToList());
            }
            var batches = new List<List<PlanEntry>>();
            foreach (var g in groups)
                for (var i = 0; i < g.Count; i += _subBatch) batches.Add(g.Skip(i).Take(_subBatch).ToList());
            return batches;
        }

        /// <summary>Applies a sub-batch. Runs of identical changes (same move, delete, hide in the same view) go in one call.</summary>
        private void ApplyBatch(List<PlanEntry> batch, Action<PlanEntry> at)
        {
            var i = 0;
            while (i < batch.Count)
            {
                var e = batch[i];
                at(e);
                var key = RunKey(e);
                if (key != null)
                {
                    var j = i + 1;
                    while (j < batch.Count && RunKey(batch[j]) == key) j++;
                    if (j - i > 1)
                    {
                        ApplyRun(batch.GetRange(i, j - i));
                        i = j;
                        continue;
                    }
                }
                ApplyEntry(e);
                i++;
            }
        }

        /// <summary>Grouping key: entries with the same key are applied with a single API call.</summary>
        private static string RunKey(PlanEntry e)
        {
            if (e.Ref > 0 || e.Id == 0) return null;
            if (e.Key == "exists" && e.Op == "delete") return "del";
            if (e.Key == "loc" && e.Op == "move" && e.Target is List<object> v) return "mv|" + string.Join(",", v.Select(x => PlanEntry.Canon(Convert.ToDouble(x, CultureInfo.InvariantCulture))));
            if (e.Key == "loc" && e.Op == "mirror") return "mi|" + Canonical(WriteKit.Dict(e.Target), "eje");
            if (e.Key == "rot" && e.Target is Dictionary<string, object> rt && rt.ContainsKey("cx")) return "ro|" + WriteKit.D(rt, "deg") + "|" + WriteKit.D(rt, "cx") + "|" + WriteKit.D(rt, "cy");
            if (e.Key.StartsWith("oculto@", StringComparison.Ordinal) && !e.Key.Contains("@@")) return e.Key + "|" + WriteKit.B(WriteKit.Dict(e.Target), "hide");
            return null;
        }

        private static string Canonical(Dictionary<string, object> t, string key) =>
            t.TryGetValue(key, out var v) && v is List<object> l ? string.Join(",", l.Select(x => PlanEntry.Canon(Convert.ToDouble(x, CultureInfo.InvariantCulture)))) : "";

        private void ApplyRun(List<PlanEntry> run)
        {
            var first = run[0];
            var ids = new List<ElementId>();
            foreach (var e in run)
            {
                if (_deleted.Contains(e.Id)) { Skip("already deleted by an earlier operation of the plan", e.Id); continue; }
                var el = _doc.GetElement(Ids.ToId(e.Id));
                if (el == null)
                {
                    if (first.Key == "exists") continue; // already deleted earlier as a dependent of another delete
                    throw new InvalidOperationException(e.Id + " no longer exists");
                }
                ids.Add(el.Id);
            }
            if (ids.Count == 0) return;
            if (first.Key == "exists")
            {
                var gone = _doc.Delete(ids);
                foreach (var g in gone) _deleted.Add(Ids.ToLong(g));
                _r.Deleted += gone.Count;
                return;
            }
            if (first.Key == "loc" && first.Op == "move")
            {
                ElementTransformUtils.MoveElements(_doc, ids, Vec((List<object>)first.Target));
            }
            else if (first.Key == "loc" && first.Op == "mirror")
            {
                ElementTransformUtils.MirrorElements(_doc, ids, MirrorPlane(WriteKit.Dict(first.Target)), false);
            }
            else if (first.Key == "rot")
            {
                var t = WriteKit.Dict(first.Target);
                var c = new XYZ(WriteKit.D(t, "cx"), WriteKit.D(t, "cy"), 0);
                ElementTransformUtils.RotateElements(_doc, ids, Line.CreateBound(c, c + XYZ.BasisZ), WriteKit.D(t, "deg") * Math.PI / 180.0);
            }
            else if (first.Key.StartsWith("oculto@", StringComparison.Ordinal))
            {
                var t = WriteKit.Dict(first.Target);
                var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
                if (WriteKit.B(t, "hide")) view.HideElements(ids); else view.UnhideElements(ids);
            }
            _r.Changed += ids.Count;
        }

        private static XYZ Vec(List<object> v) =>
            new XYZ(Convert.ToDouble(v[0], CultureInfo.InvariantCulture), Convert.ToDouble(v[1], CultureInfo.InvariantCulture), v.Count > 2 ? Convert.ToDouble(v[2], CultureInfo.InvariantCulture) : 0);

        /// <summary>Vertical plane containing the mirror axis [x1,y1,x2,y2].</summary>
        private static Plane MirrorPlane(Dictionary<string, object> t)
        {
            var a = (List<object>)t["eje"];
            double x1 = Convert.ToDouble(a[0], CultureInfo.InvariantCulture), y1 = Convert.ToDouble(a[1], CultureInfo.InvariantCulture);
            double x2 = Convert.ToDouble(a[2], CultureInfo.InvariantCulture), y2 = Convert.ToDouble(a[3], CultureInfo.InvariantCulture);
            var normal = new XYZ(y2 - y1, -(x2 - x1), 0).Normalize();
            return Plane.CreateByNormalAndOrigin(normal, new XYZ(x1, y1, 0));
        }

        private void Skip(string why, long id)
        {
            if (!_skipped.TryGetValue(why, out var l)) _skipped[why] = l = new List<long>();
            l.Add(id);
        }

        private void Record(int op, IEnumerable<ElementId> ids)
        {
            if (!_plan.CreatedByOp.TryGetValue(op, out var l)) _plan.CreatedByOp[op] = l = new List<long>();
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                var v = Ids.ToLong(id);
                l.Add(v);
                _plan.Created.Add(v);
            }
        }

        private void ApplyEntry(PlanEntry entry)
        {
            if (entry.Ref > 0) { ApplyTemplate(entry); return; }
            if (entry.Key == "nuevo")
            {
                var target = WriteKit.Dict(entry.Target);
                var ids = Create(entry.Op, target, 0, entry.OpIndex);
                if (entry.Op == "place" && target.ContainsKey("z_verificar"))
                {
                    var made = ids.Single();
                    _sourcePlacements.Add((entry, made));
                    Record(entry.OpIndex, new[] { made });
                }
                else Record(entry.OpIndex, ids);
                return;
            }
            ApplyExisting(entry, entry.Id, entry.Target, false);
        }

        private readonly List<(PlanEntry Entry, ElementId Id)> _sourcePlacements = new List<(PlanEntry, ElementId)>();

        private string VerifySourcePlacements()
        {
            var expected = _plan.Entries.Count(e => e.Op == "place" && e.Key == "nuevo" && WriteKit.Dict(e.Target).ContainsKey("z_verificar"));
            if (expected != _sourcePlacements.Count) return "Placement: elements are missing for verification";
            const double oneMillimeter = 1.0 / 304.8;
            foreach (var (entry, id) in _sourcePlacements)
            {
                var t = WriteKit.Dict(entry.Target);
                var f = _doc.GetElement(id) as FamilyInstance;
                var point = (f?.Location as LocationPoint)?.Point;
                var wanted = new XYZ(WriteKit.D(t, "x"), WriteKit.D(t, "y"), WriteKit.D(t, "z_verificar"));
                if (point == null || Ids.ToLong(f.GetTypeId()) != WriteKit.L(t, "sym") ||
                    Ids.ToLong(f.LevelId) != WriteKit.L(t, "lvl") || point.DistanceTo(wanted) > oneMillimeter ||
                    (t.ContainsKey("ws") && f.WorksetId.IntegerValue != WriteKit.L(t, "ws")))
                    return "Placement: " + (WriteKit.S(t, "item_fuente") ?? Where(entry)) + " (id " + Ids.ToLong(id) + ") does not match in type, level, position or workset";
                if (t.TryGetValue("params", out var parameters) && parameters is Dictionary<string, object> expectedParams)
                    foreach (var pair in expectedParams)
                    {
                        var parameter = f.LookupParameter(pair.Key);
                        var value = pair.Value;
                        if (value is Dictionary<string, object> raw && raw.TryGetValue("raw", out var input) &&
                            (parameter == null || !WriteKit.TryTarget(_doc, parameter, input, out value, out _, out _)))
                            return "Placement: could not re-read parameter " + pair.Key + " on " + Ids.ToLong(id);
                        if (!PlanEntry.Same(WriteKit.ParamValue(parameter), PlanEntry.Canon(value)))
                            return "Placement: parameter " + pair.Key + " does not match on " + Ids.ToLong(id);
                    }
            }
            return null;
        }

        /// <summary>Template entry (ids=@n): applied to each element that operation n created.</summary>
        private void ApplyTemplate(PlanEntry entry)
        {
            if (!_plan.CreatedByOp.TryGetValue(entry.Ref, out var created) || created.Count == 0)
            {
                _notes.Add(OpCatalog.NameOf(entry.Op) + " (op " + entry.OpIndex + "): @" + entry.Ref + " created nothing");
                return;
            }
            var t = WriteKit.Dict(entry.Target);
            foreach (var id in created.ToList())
            {
                switch (entry.Key)
                {
                    case "nuevo":
                        Record(entry.OpIndex, Create(entry.Op, t, id, entry.OpIndex));
                        break;
                    case "tag":
                        Record(entry.OpIndex, TagFromTemplate(id, t));
                        break;
                    default:
                        ApplyExisting(entry, id, entry.Target, true);
                        break;
                }
            }
        }

        // =====================================================================================================
        // Changes to existing elements
        // =====================================================================================================

        private void ApplyExisting(PlanEntry entry, long id, object target, bool fromTemplate)
        {
            if (_deleted.Contains(id)) { Skip("already deleted by an earlier operation of the plan", id); return; }
            var e = _doc.GetElement(Ids.ToId(id));
            if (entry.Key == "exists")
            {
                if (e == null) return; // already deleted earlier as a dependent of another delete
                if (entry.Op == "ungroup")
                {
                    if (!(e is Group g)) throw new InvalidOperationException("is not a group");
                    g.UngroupMembers();
                    _r.Changed++;
                    return;
                }
                var gone = _doc.Delete(e.Id);
                foreach (var x in gone) _deleted.Add(Ids.ToLong(x));
                _r.Deleted += gone.Count;
                return;
            }
            if (e == null) throw new InvalidOperationException("the element no longer exists");

            if (entry.Op == "set_param" || entry.Op == "set_type_param" || (entry.Op == "set_values" && !Special(entry.Key)))
            {
                SetNamedParam(e, entry.Key, target, fromTemplate);
                return;
            }
            var t = WriteKit.Dict(target);
            var v = e as View;
            switch (entry.Key)
            {
                case "type":
                {
                    var tid = Ids.ToId(Convert.ToInt64(target, CultureInfo.InvariantCulture));
                    bool valid;
                    try { valid = e.IsValidType(tid); } catch (Exception) { valid = false; }
                    if (!valid)
                    {
                        if (fromTemplate) { Skip("the type is not valid for that element", id); return; }
                        throw new InvalidOperationException("the type is no longer valid for that element");
                    }
                    e.ChangeTypeId(tid);
                    break;
                }
                case "elev":
                {
                    var p = WriteKit.ElevParam(e);
                    if (p == null || p.IsReadOnly)
                    {
                        if (fromTemplate) { Skip("has no editable elevation", id); return; }
                        throw new InvalidOperationException("has no editable elevation");
                    }
                    if (!p.Set(Convert.ToDouble(target, CultureInfo.InvariantCulture))) throw new InvalidOperationException("Revit did not accept the elevation");
                    break;
                }
                case "ws":
                {
                    var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly)
                    {
                        if (fromTemplate) { Skip("its workset cannot be changed", id); return; }
                        throw new InvalidOperationException("its workset cannot be changed");
                    }
                    if (!p.Set((int)Convert.ToInt64(target, CultureInfo.InvariantCulture))) throw new InvalidOperationException("Revit did not accept the workset");
                    break;
                }
                case "lvl":
                {
                    var p = WriteKit.LevelParam(e);
                    if (p == null || p.IsReadOnly) throw new InvalidOperationException("its level cannot be changed");
                    if (!p.Set(Ids.ToId(Convert.ToInt64(target, CultureInfo.InvariantCulture)))) throw new InvalidOperationException("Revit did not accept the level");
                    break;
                }
                case "loc":
                    if (target is List<object> mv) ElementTransformUtils.MoveElement(_doc, e.Id, Vec(mv));
                    else ElementTransformUtils.MirrorElements(_doc, new List<ElementId> { e.Id }, MirrorPlane(t), false);
                    break;
                case "rot":
                {
                    XYZ c;
                    if (t.ContainsKey("cx")) c = new XYZ(WriteKit.D(t, "cx"), WriteKit.D(t, "cy"), 0);
                    else c = WriteKit.LocationOf(e) ?? throw new InvalidOperationException("has no location");
                    ElementTransformUtils.RotateElement(_doc, e.Id, Line.CreateBound(c, c + XYZ.BasisZ), WriteKit.D(t, "deg") * Math.PI / 180.0);
                    break;
                }
                case "head":
                    MoveTag(e as IndependentTag ?? throw new InvalidOperationException("is not a tag"), t);
                    break;
                case "llamada_luminaria":
                    RefreshCallout(entry, e as TextNote ?? throw new InvalidOperationException("the note no longer exists"));
                    break;
                case "plantilla":
                    if (v == null) throw new InvalidOperationException("is not a view");
                    var tpl = Convert.ToInt64(target, CultureInfo.InvariantCulture);
                    v.ViewTemplateId = tpl == -1 ? ElementId.InvalidElementId : Ids.ToId(tpl);
                    break;
                case "rango":
                    SetRange(v as ViewPlan ?? throw new InvalidOperationException("view range only exists in floor plans"), t, fromTemplate);
                    break;
                case "escala":
                    if (v == null) throw new InvalidOperationException("is not a view");
                    v.Scale = (int)Convert.ToInt64(target, CultureInfo.InvariantCulture);
                    break;
                case "detalle":
                    if (v == null) throw new InvalidOperationException("is not a view");
                    var d = Convert.ToString(target, CultureInfo.InvariantCulture);
                    v.DetailLevel = d == "fino" ? ViewDetailLevel.Fine : d == "medio" ? ViewDetailLevel.Medium : ViewDetailLevel.Coarse;
                    break;
                case "nombre":
                    e.Name = Convert.ToString(target, CultureInfo.InvariantCulture);
                    break;
                case "recorte":
                    if (v == null) throw new InvalidOperationException("is not a view");
                    v.CropBoxActive = target is bool b ? b : Convert.ToInt64(target, CultureInfo.InvariantCulture) != 0;
                    break;
                case "scope_box":
                {
                    var p = e.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    if (p == null || p.IsReadOnly)
                    {
                        if (fromTemplate) { Skip("that view does not support a scope box", id); return; }
                        throw new InvalidOperationException("that view does not support a scope box");
                    }
                    var sb = Convert.ToInt64(target, CultureInfo.InvariantCulture);
                    if (!p.Set(sb == -1 ? ElementId.InvalidElementId : Ids.ToId(sb))) throw new InvalidOperationException("Revit did not accept the scope box");
                    break;
                }
                case "caja_seccion":
                {
                    if (!(v is View3D v3)) { if (fromTemplate) { Skip("caja_seccion only in 3D views", id); return; } throw new InvalidOperationException("is not a 3D view"); }
                    var l = (List<object>)target;
                    v3.SetSectionBox(Box((List<object>)l[0], (List<object>)l[1]));
                    break;
                }
                default:
                    if (entry.Key.StartsWith("oculto@", StringComparison.Ordinal))
                    {
                        var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
                        var list = new List<ElementId> { e.Id };
                        bool can;
                        try { can = e.CanBeHidden(view); } catch (Exception) { can = false; }
                        if (!can) { Skip("cannot be hidden in that view", id); return; }
                        if (WriteKit.B(t, "hide")) view.HideElements(list); else view.UnhideElements(list);
                    }
                    else if (entry.Key.StartsWith("graficos@", StringComparison.Ordinal))
                    {
                        var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
                        view.SetElementOverrides(e.Id, PlanBuilder.Merge(_doc, view.GetElementOverrides(e.Id), t));
                    }
                    else if (entry.Key.StartsWith("cat_oculta:", StringComparison.Ordinal))
                    {
                        if (v == null) throw new InvalidOperationException("is not a view");
                        var cat = Ids.ToId(WriteKit.L(t, "cat"));
                        if (!v.CanCategoryBeHidden(cat)) { Skip("that category cannot be hidden in that view", id); return; }
                        v.SetCategoryHidden(cat, WriteKit.B(t, "hide"));
                    }
                    else if (entry.Key.StartsWith("cat_graficos:", StringComparison.Ordinal))
                    {
                        if (v == null) throw new InvalidOperationException("is not a view");
                        var cat = Ids.ToId(WriteKit.L(t, "cat"));
                        v.SetCategoryOverrides(cat, PlanBuilder.Merge(_doc, v.GetCategoryOverrides(cat), t));
                    }
                    else
                    {
                        SetNamedParam(e, entry.Key, target, fromTemplate); // old plans
                        return;
                    }
                    break;
            }
            _r.Changed++;
        }

        private void SetNamedParam(Element e, string name, object target, bool fromTemplate)
        {
            var p = e.LookupParameter(name);
            if (p == null || p.IsReadOnly)
            {
                if (fromTemplate) { Skip(p == null ? "does not have the parameter '" + name + "'" : "'" + name + "' is read-only", Ids.ToLong(e.Id)); return; }
                throw new InvalidOperationException(p == null ? "no parameter '" + name + "'" : "'" + name + "' is read-only");
            }
            var value = target;
            if (target is Dictionary<string, object> raw && raw.ContainsKey("raw"))
            {
                // Unconverted value (template @n or place without another instance to validate against): converted now against the real parameter.
                if (!WriteKit.TryTarget(_doc, p, raw["raw"], out value, out _, out var why))
                {
                    if (fromTemplate) { Skip("'" + name + "': " + why, Ids.ToLong(e.Id)); return; }
                    throw new InvalidOperationException("'" + name + "': " + why);
                }
            }
            WriteKit.SetParam(p, value);
            _r.Changed++;
        }

        private static BoundingBoxXYZ Box(List<object> a, List<object> b)
        {
            double D(List<object> l, int i) => Convert.ToDouble(l[i], CultureInfo.InvariantCulture);
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(D(a, 0), D(b, 0)), Math.Min(D(a, 1), D(b, 1)), Math.Min(D(a, 2), D(b, 2))),
                Max = new XYZ(Math.Max(D(a, 0), D(b, 0)), Math.Max(D(a, 1), D(b, 1)), Math.Max(D(a, 2), D(b, 2))),
            };
        }

        private void SetRange(ViewPlan vp, Dictionary<string, object> t, bool fromTemplate)
        {
            var r = vp.GetViewRange();
            foreach (var kv in t)
            {
                PlanViewPlane plane;
                switch (kv.Key)
                {
                    case "top": plane = PlanViewPlane.TopClipPlane; break;
                    case "corte": plane = PlanViewPlane.CutPlane; break;
                    case "fondo": plane = PlanViewPlane.BottomClipPlane; break;
                    case "profundidad": plane = PlanViewPlane.ViewDepthPlane; break;
                    default: continue;
                }
                var pair = (List<object>)kv.Value;
                var lid = Convert.ToInt64(pair[0], CultureInfo.InvariantCulture);
                var off = Convert.ToDouble(pair[1], CultureInfo.InvariantCulture);
                ElementId level;
                switch (lid)
                {
                    case -101: level = PlanViewRange.Current; break;
                    case -102: level = PlanViewRange.LevelAbove; break;
                    case -103: level = PlanViewRange.LevelBelow; break;
                    case -104: level = PlanViewRange.Unlimited; break;
                    default: level = Ids.ToId(lid); break;
                }
                r.SetLevelId(plane, level);
                if (lid != -104) r.SetOffset(plane, off);
            }
            vp.SetViewRange(r);
        }

        private void MoveTag(IndependentTag tag, Dictionary<string, object> t)
        {
            var leader = WriteKit.B(t, "leader");
            if (tag.HasLeader != leader) tag.HasLeader = leader;
            var orient = WriteKit.B(t, "vertical") ? TagOrientation.Vertical : TagOrientation.Horizontal;
            if (tag.TagOrientation != orient) tag.TagOrientation = orient;
            var z = WriteKit.D(t, "hz", 0);
            tag.TagHeadPosition = new XYZ(WriteKit.D(t, "hx"), WriteKit.D(t, "hy"), z);
            if (!leader) return;
            var end = WriteKit.P(t, "ex");
            var elbow = WriteKit.P(t, "co");
            if (end == null && elbow == null) return;
            if (end != null && tag.LeaderEndCondition != LeaderEndCondition.Free)
            {
                if (!tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free)) throw new InvalidOperationException("that tag does not support a free leader end");
                tag.LeaderEndCondition = LeaderEndCondition.Free;
            }
            foreach (var r in tag.GetTaggedReferences())
            {
                if (end != null) tag.SetLeaderEnd(r, new XYZ(end.X, end.Y, z));
                if (elbow != null) tag.SetLeaderElbow(r, new XYZ(elbow.X, elbow.Y, z));
            }
        }

        // =====================================================================================================
        // Creation
        // =====================================================================================================

        /// <summary>Creates what a "nuevo" entry asks for. <paramref name="src"/> &gt; 0: the source element comes from @n (copy/mirror templates).</summary>
        private IEnumerable<ElementId> Create(string op, Dictionary<string, object> t, long src, int opIndex)
        {
            switch (op)
            {
                case "copy": return CopyOne(t, src > 0 ? src : WriteKit.L(t, "src"));
                case "mirror":
                {
                    var s = src > 0 ? src : WriteKit.L(t, "src");
                    var e = _doc.GetElement(Ids.ToId(s)) ?? throw new InvalidOperationException(s + " no longer exists");
                    var copies = ElementTransformUtils.MirrorElements(_doc, new List<ElementId> { e.Id }, MirrorPlane(t), true);
                    return copies ?? (IEnumerable<ElementId>)new ElementId[0];
                }
                case "place": return new[] { Place(t) };
                case "tag": return new[] { CreateTag(t) };
                case "group_create": return new[] { GroupCreate(t) };
                case "group_place": return new[] { GroupPlace(t) };
                case "group_variant": return new[] { RebuildGroupVariant(t) };
                case "view_create": return new[] { ViewCreate(t) };
                case "sheet_create": return new[] { SheetCreate(t) };
                case "sheet_place": return new[] { SheetPlace(t) };
                case "sheet_set": return new[] { SheetSetCreate(t) };
                case "text_note": return new[] { TextNoteCreate(t) };
                case "annotation_rebind": return new[] { AnnotationRebind(t) };
                case "filled_region": return new[] { FilledRegionCreate(t) };
                case "detail_line": case "detail_rect": case "detail_polygon": return DetailCurvesCreate(t);
                case "detail_arc": return new[] { DetailArcCreate(t) };
                case "detail_component": return new[] { DetailComponentCreate(t) };
                case "dimension": return new[] { DimensionCreate(t) };
                default:
                    var made = ModelCreate(op, t, opIndex);
                    if (made != null) return made;
                    throw new InvalidOperationException("don't know how to create with " + op);
            }
        }

        private IEnumerable<ElementId> CopyOne(Dictionary<string, object> t, long srcId)
        {
            var e = _doc.GetElement(Ids.ToId(srcId)) ?? throw new InvalidOperationException(srcId + " no longer exists");
            if (!t.ContainsKey("nivel"))
            {
                var v = (List<object>)t["v"];
                return ElementTransformUtils.CopyElement(_doc, e.Id, Vec(v));
            }
            var lvl = _doc.GetElement(Ids.ToId(WriteKit.L(t, "nivel"))) as Level ?? throw new InvalidOperationException("the level no longer exists");
            var old = WriteKit.LevelOf(_doc, e);
            if (old == null) { Skip("has no level", srcId); return new ElementId[0]; }
            if (old.Id == lvl.Id) { Skip("already on " + lvl.Name, srcId); return new ElementId[0]; }
            var dz = lvl.ProjectElevation - old.ProjectElevation;
            var ep = WriteKit.ElevParam(e);
            var offset = ep != null && !ep.IsReadOnly ? ep.AsDouble() : (double?)null;
            var copies = ElementTransformUtils.CopyElement(_doc, e.Id, new XYZ(0, 0, dz));
            foreach (var id in copies)
            {
                var c = _doc.GetElement(id);
                var lp = c == null ? null : WriteKit.LevelParam(c);
                if (lp == null || lp.IsReadOnly) continue; // hosted: raised by dz but still associated with the original level (warned during planning)
                lp.Set(lvl.Id);
                // Whatever happens to the offset when the level changes, it ends up like the original: same height above its level.
                var cp = WriteKit.ElevParam(c);
                if (offset.HasValue && cp != null && !cp.IsReadOnly) cp.Set(offset.Value);
            }
            return copies;
        }

        private ElementId Place(Dictionary<string, object> t)
        {
            var sym = _doc.GetElement(Ids.ToId(WriteKit.L(t, "sym"))) as FamilySymbol ?? throw new InvalidOperationException("the type no longer exists");
            var lvl = _doc.GetElement(Ids.ToId(WriteKit.L(t, "lvl"))) as Level ?? throw new InvalidOperationException("the level no longer exists");
            if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
            var p = new XYZ(WriteKit.D(t, "x"), WriteKit.D(t, "y"), lvl.ProjectElevation);
            var fi = _doc.Create.NewFamilyInstance(p, sym, lvl, StructuralType.NonStructural);
            if (t.ContainsKey("elev"))
            {
                var ep = WriteKit.ElevParam(fi);
                if (ep == null || ep.IsReadOnly) throw new InvalidOperationException("the new instance has no editable elevation");
                ep.Set(WriteKit.D(t, "elev"));
            }
            if (t.ContainsKey("rot"))
            {
                var at = (fi.Location as LocationPoint)?.Point ?? p;
                ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(at, at + XYZ.BasisZ), WriteKit.D(t, "rot") * Math.PI / 180.0);
            }
            if (t.ContainsKey("ws"))
            {
                var wp = fi.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (wp != null && !wp.IsReadOnly) wp.Set((int)WriteKit.L(t, "ws"));
            }
            if (t.TryGetValue("params", out var pr) && pr is Dictionary<string, object> prms)
                foreach (var kv in prms) SetNamedParam(fi, kv.Key, kv.Value, false);
            return fi.Id;
        }

        private ElementId CreateTag(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var elemId = WriteKit.L(t, "elem");
            var elem = _doc.GetElement(Ids.ToId(elemId)) ?? throw new InvalidOperationException(elemId + " no longer exists");
            var kit = Kit(view);
            if (kit.Tagged.Contains(elemId)) { Skip("already tagged in that view", elemId); return ElementId.InvalidElementId; }
            var head = new XYZ(WriteKit.D(t, "hx"), WriteKit.D(t, "hy"), WriteKit.D(t, "hz", 0));
            var tag = NewTag(view, elem, WriteKit.L(t, "ttype"), WriteKit.B(t, "leader"), WriteKit.B(t, "vertical"), head);
            kit.Remember(elemId, TagKit.TagCategoryId(elem), null);
            return tag.Id;
        }

        private IEnumerable<ElementId> TagFromTemplate(long elemId, Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var elem = _doc.GetElement(Ids.ToId(elemId));
            if (elem == null) { Skip("no longer exists", elemId); return new ElementId[0]; }
            var kit = Kit(view);
            if (kit.Tagged.Contains(elemId)) { Skip("already tagged in that view", elemId); return new ElementId[0]; }
            var forced = WriteKit.L(t, "ttype");
            var forcedType = forced > 0 ? _doc.GetElement(Ids.ToId(forced)) as ElementType : null;
            double[] offset = null;
            if (t.TryGetValue("offset", out var o) && o is List<object> ol && ol.Count >= 2)
                offset = new[] { Convert.ToDouble(ol[0], CultureInfo.InvariantCulture), Convert.ToDouble(ol[1], CultureInfo.InvariantCulture) };
            bool? leader = t.ContainsKey("leader") ? WriteKit.B(t, "leader") : (bool?)null;
            if (!kit.TryChoose(elem, forcedType, offset, leader, WriteKit.S(t, "orientacion"), Math.Max(1, view.Scale), out var ch, out var why))
            {
                Skip(why, elemId);
                return new ElementId[0];
            }
            if (ch.Note != null) _notes.Add("tag: " + ch.Note);
            var tag = NewTag(view, elem, ch.TypeId, ch.Leader, ch.Vertical, ch.Head);
            kit.Remember(elemId, TagKit.TagCategoryId(elem), null);
            return new[] { tag.Id };
        }

        private IndependentTag NewTag(View view, Element elem, long typeId, bool leader, bool vertical, XYZ head)
        {
            var tid = Ids.ToId(typeId);
            if (_doc.GetElement(tid) is FamilySymbol fs && !fs.IsActive) { fs.Activate(); _doc.Regenerate(); }
            var tag = IndependentTag.Create(_doc, tid, view.Id, new Reference(elem), leader, vertical ? TagOrientation.Vertical : TagOrientation.Horizontal, head);
            if (tag == null) throw new InvalidOperationException("Revit did not create the tag for " + Ids.ToLong(elem.Id));
            return tag;
        }

        private ElementId GroupCreate(Dictionary<string, object> t)
        {
            var ids = new List<ElementId>();
            foreach (var id in WriteKit.Longs(t, "ids")) ids.Add(Ids.ToId(id));
            foreach (var r in WriteKit.Longs(t, "refs"))
                if (_plan.CreatedByOp.TryGetValue((int)r, out var made)) ids.AddRange(made.Select(Ids.ToId));
            ids = ids.Where(x => _doc.GetElement(x) != null).Distinct().ToList();
            if (ids.Count == 0) throw new InvalidOperationException("nothing left to group");
            var g = _doc.Create.NewGroup(ids) ?? throw new InvalidOperationException("Revit did not create the group");
            var name = WriteKit.S(t, "nombre");
            if (name != null) g.GroupType.Name = name;
            return g.Id;
        }

        private readonly List<(Dictionary<string, object> Target, ElementId Id)> _managedGroups =
            new List<(Dictionary<string, object>, ElementId)>();
        private readonly List<(Dictionary<string, object> Target, ElementId Id)> _groupVariants =
            new List<(Dictionary<string, object>, ElementId)>();

        private ElementId RebuildGroupVariant(Dictionary<string, object> target)
        {
            var group = GroupVariant.Rebuild(_doc, target);
            _deleted.Add(WriteKit.L(target, "grupo"));
            _r.Deleted++;
            _groupVariants.Add((target, group.Id));
            return group.Id;
        }

        private ElementId GroupPlace(Dictionary<string, object> t)
        {
            GroupType gt;
            if (t.ContainsKey("gtype")) gt = _doc.GetElement(Ids.ToId(WriteKit.L(t, "gtype"))) as GroupType;
            else gt = (_doc.GetElement(Ids.ToId(FirstCreated((int)WriteKit.L(t, "gref"), "tipo"))) as Group)?.GroupType;
            if (gt == null) throw new InvalidOperationException("the group type no longer exists");
            var point = GroupPlacement.Point(t);
            if (WriteKit.B(t, "reutilizar") && GroupPlacement.AtPoint(_doc, gt.Id, point).Count > 0)
                throw new InvalidOperationException("a group appeared at the planned point; plan again");
            var g = _doc.Create.PlaceGroup(point, gt) ?? throw new InvalidOperationException("Revit did not place the group");
            if (t.ContainsKey("nivel"))
            {
                var wanted = Ids.ToId(WriteKit.L(t, "nivel"));
                var parameter = g.get_Parameter(BuiltInParameter.GROUP_LEVEL);
                if (parameter == null || (parameter.AsElementId() != wanted && (parameter.IsReadOnly || !parameter.Set(wanted))))
                    throw new InvalidOperationException("Revit did not accept the group's reference level");
                _doc.Regenerate();
                if (g.Location is LocationPoint location && location.Point.DistanceTo(point) > Tol)
                    ElementTransformUtils.MoveElement(_doc, g.Id, point - location.Point);
            }
            if (t.ContainsKey("rot"))
            {
                var location = (LocationPoint)g.Location;
                location.Rotate(Line.CreateBound(point, point + XYZ.BasisZ), WriteKit.D(t, "rot") * Math.PI / 180 - location.Rotation);
            }
            if (t.ContainsKey("ws"))
            {
                var workset = g.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                var wanted = (int)WriteKit.L(t, "ws");
                if (workset == null || (workset.AsInteger() != wanted && (workset.IsReadOnly || !workset.Set(wanted))))
                    throw new InvalidOperationException("Revit did not accept the group's workset");
            }
            if (t.ContainsKey("nivel") || t.ContainsKey("rot") || t.ContainsKey("ws") || t.ContainsKey("firma_miembros"))
                _managedGroups.Add((t, g.Id));
            return g.Id;
        }

        private ElementId ViewCreate(Dictionary<string, object> t)
        {
            var kind = WriteKit.S(t, "tipo");
            var vft = Ids.ToId(WriteKit.L(t, "vft", -1));
            View v;
            switch (kind)
            {
                case "plano":
                case "rcp":
                    v = ViewPlan.Create(_doc, vft, Ids.ToId(WriteKit.L(t, "nivel")));
                    break;
                case "seccion":
                {
                    var caja = (List<object>)t["caja"];
                    var a = (List<object>)caja[0]; var b = (List<object>)caja[1];
                    double ax = N(a, 0), ay = N(a, 1), az = N(a, 2), bx = N(b, 0), by = N(b, 1), bz = N(b, 2);
                    var dir = new XYZ(bx - ax, by - ay, 0);
                    var len = dir.GetLength();
                    dir = dir.Normalize();
                    var tf = Transform.Identity;
                    tf.Origin = new XYZ((ax + bx) / 2, (ay + by) / 2, 0);
                    tf.BasisX = dir;
                    tf.BasisY = XYZ.BasisZ;
                    tf.BasisZ = dir.CrossProduct(XYZ.BasisZ); // facing direction: the view looks to the left of the line
                    var depth = WriteKit.D(t, "prof", 6.0);
                    var box = new BoundingBoxXYZ
                    {
                        Transform = tf,
                        Min = new XYZ(-len / 2, Math.Min(az, bz), -depth),
                        Max = new XYZ(len / 2, Math.Max(az, bz), 0),
                    };
                    v = ViewSection.CreateSection(_doc, vft, box);
                    break;
                }
                case "callout":
                {
                    var caja = (List<object>)t["caja"];
                    var a = (List<object>)caja[0]; var b = (List<object>)caja[1];
                    v = ViewSection.CreateCallout(_doc, Ids.ToId(WriteKit.L(t, "origen")), vft, new XYZ(N(a, 0), N(a, 1), 0), new XYZ(N(b, 0), N(b, 1), 0));
                    break;
                }
                case "dibujo":
                {
                    var vd = ViewDrafting.Create(_doc, vft);
                    if (t.ContainsKey("escala")) vd.Scale = (int)WriteKit.L(t, "escala");
                    v = vd;
                    break;
                }
                case "3d":
                {
                    var v3 = View3D.CreateIsometric(_doc, vft);
                    if (t.TryGetValue("caja", out var c) && c is List<object> caja) v3.SetSectionBox(Box((List<object>)caja[0], (List<object>)caja[1]));
                    v = v3;
                    break;
                }
                case "duplicar":
                {
                    var src = _doc.GetElement(Ids.ToId(WriteKit.L(t, "origen"))) as View ?? throw new InvalidOperationException("the source view no longer exists");
                    var opt = WriteKit.S(t, "opcion");
                    var dup = opt == "detalles" ? ViewDuplicateOption.WithDetailing : opt == "dependiente" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.Duplicate;
                    v = _doc.GetElement(src.Duplicate(dup)) as View;
                    break;
                }
                default: throw new InvalidOperationException("unknown view type: " + kind);
            }
            if (v == null) throw new InvalidOperationException("Revit did not create the view");
            var name = WriteKit.S(t, "nombre");
            if (name != null) v.Name = name;
            if (t.ContainsKey("plantilla")) v.ViewTemplateId = Ids.ToId(WriteKit.L(t, "plantilla"));
            return v.Id;
        }

        private static double N(List<object> l, int i) => i < l.Count ? Convert.ToDouble(l[i], CultureInfo.InvariantCulture) : 0;

        private ElementId SheetCreate(Dictionary<string, object> t)
        {
            var s = ViewSheet.Create(_doc, Ids.ToId(WriteKit.L(t, "tb"))) ?? throw new InvalidOperationException("Revit did not create the sheet");
            s.SheetNumber = WriteKit.S(t, "numero");
            s.Name = WriteKit.S(t, "nombre");
            return s.Id;
        }

        /// <summary>ViewSheetSet via PrintManager (leaves PrintRange=Select in the document, as the UI does). Verifies the saved views.</summary>
        private ElementId SheetSetCreate(Dictionary<string, object> t)
        {
            var name = WriteKit.S(t, "nombre");
            var ids = ((List<object>)t["hojas"]).Select(h => Ids.ToId(Convert.ToInt64(h, CultureInfo.InvariantCulture))).ToList();
            if (t.ContainsKey("reemplaza")) _doc.Delete(Ids.ToId(WriteKit.L(t, "reemplaza")));
            var pm = _doc.PrintManager;
            pm.PrintRange = PrintRange.Select;
            var vss = pm.ViewSheetSetting;
            var set = new ViewSet();
            foreach (var id in ids) set.Insert(_doc.GetElement(id) as ViewSheet ?? throw new InvalidOperationException(Ids.ToLong(id) + " is no longer a sheet"));
            vss.CurrentViewSheetSet.Views = set;
            vss.SaveAs(name);
            var saved = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal)) ?? throw new InvalidOperationException("Revit did not save the set '" + name + "'");
            var got = new HashSet<long>(saved.Views.Cast<View>().Select(v => Ids.ToLong(v.Id)));
            if (got.Count != ids.Count || ids.Any(i => !got.Contains(Ids.ToLong(i)))) throw new InvalidOperationException("the saved set has " + got.Count + " views, expected " + ids.Count);
            t.TryGetValue("preset_pdf", out var preset);
            SheetSetPdf.Write(saved, ids, preset);
            var stored = SheetSetPdf.Read(saved);
            if (!ids.SequenceEqual(stored.Order) || OpSemantics.Canonical(preset) != OpSemantics.Canonical(stored.Preset))
                throw new InvalidOperationException("Revit did not keep the set's order or PDF preset");
            return saved.Id;
        }

        private ElementId SheetPlace(Dictionary<string, object> t)
        {
            var sheet = ViewOf(t, "hoja", "hoja_ref") as ViewSheet ?? throw new InvalidOperationException("the sheet no longer exists");
            var view = ViewOf(t, "vista", "vista_ref") ?? throw new InvalidOperationException("the view no longer exists");
            if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, view.Id))
                throw new InvalidOperationException("Revit does not allow placing '" + view.Name + "' on " + sheet.SheetNumber + " (is it already on another sheet, or is it a template?)");
            XYZ at;
            if (t.ContainsKey("x")) at = new XYZ(WriteKit.D(t, "x"), WriteKit.D(t, "y"), 0);
            else at = SheetCenter(sheet);
            var vp = Viewport.Create(_doc, sheet.Id, view.Id, at) ?? throw new InvalidOperationException("Revit did not create the viewport");
            if (t.ContainsKey("vptype")) vp.ChangeTypeId(Ids.ToId(WriteKit.L(t, "vptype")));
            return vp.Id;
        }

        /// <summary>Titleblock center (or the sheet outline center if it has none).</summary>
        private XYZ SheetCenter(ViewSheet sheet)
        {
            try
            {
                var tb = new FilteredElementCollector(_doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                var bb = tb?.get_BoundingBox(sheet);
                if (bb != null) return new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, 0);
            }
            catch (Exception) { }
            try
            {
                var o = sheet.Outline;
                return new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
            }
            catch (Exception) { return new XYZ(1.75, 1.25, 0); }
        }

        /// <summary>Annotation point: in plans and sheets, model (x, y); in sections and elevations, x along the view from its origin and y = height.</summary>
        private static XYZ ViewPoint(View view, double x, double y)
        {
            if (view is ViewPlan vp)
            {
                double z = 0;
                try { z = vp.GenLevel?.ProjectElevation ?? 0; } catch (Exception) { }
                return new XYZ(x, y, z);
            }
            if (view is ViewSection || view.ViewType == ViewType.Elevation)
                return view.Origin + view.RightDirection * x + view.UpDirection * y;
            return new XYZ(x, y, 0);
        }

        private ElementId TextNoteCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var config = t.TryGetValue("llamada", out var data) ? data as Dictionary<string, object> : null;
            if (config != null && LightingCallout.InView(_doc, view.Id)
                .Any(existing => Arg.GetString(LightingCallout.Read(existing), "uid") == Arg.GetString(config, "uid")))
                throw new InvalidOperationException("the fixture already has a managed callout in the view");
            var note = TextNote.Create(_doc, view.Id, ViewPoint(view, WriteKit.D(t, "x"), WriteKit.D(t, "y")), WriteKit.S(t, "texto") ?? "", Ids.ToId(WriteKit.L(t, "ttype")));
            if (note == null) throw new InvalidOperationException("Revit did not create the note");
            LightingCallout.Format(note, t);
            if (config != null)
            {
                var state = LightingCallout.Observe(_doc, config);
                if (LightingCallout.Fingerprint(config, state.Text, state.Point, state.TypeUid) != WriteKit.S(t, "huella_origen"))
                    throw new InvalidOperationException("the source fixture changed after the note was planned");
                LightingCallout.Write(note, config, state.Point, state.TypeUid);
            }
            return note.Id;
        }

        private void RefreshCallout(PlanEntry entry, TextNote note)
        {
            var config = LightingCallout.Read(note) ?? throw new InvalidOperationException("the note lost its link");
            var state = LightingCallout.Observe(_doc, config);
            var source = LightingCallout.Fingerprint(config, state.Text, state.Point, state.TypeUid);
            if (LightingCallout.NoteFingerprint(note, config, source) != entry.Before)
                throw new InvalidOperationException("the note or the fixture changed after planning");
            var view = _doc.GetElement(note.OwnerViewId) as View ?? throw new InvalidOperationException("the view no longer exists");
            note.Coord = LightingCallout.Reposition(note, view, config, state.Point);
            if (note.Text != state.Text)
            {
                note.Text = state.Text;
                LightingCallout.Format(note, config);
            }
            LightingCallout.Write(note, config, state.Point, state.TypeUid);
        }

        private readonly List<(ElementId OldId, ElementId NewId, ElementId ViewId, List<string> References)> _replacementAnnotations =
            new List<(ElementId, ElementId, ElementId, List<string>)>();

        private (Element Original, List<Reference> Resolved) ReadAnnotationTargets(Dictionary<string, object> data)
        {
            var old = _doc.GetElement(Ids.ToId(WriteKit.L(data, "original")));
            if (old == null || old.Pinned || AnnotationRepair.Fingerprint(old) != WriteKit.S(data, "original_firma"))
                throw new QueryException("the original annotation changed after planning");
            var targets = AnnotationRepair.ResolveTargets(_doc, old, data);
            if (!targets.Select(r => r.ConvertToStableRepresentation(_doc)).SequenceEqual(Arg.GetStringList(data, "refs_destino")))
                throw new QueryException("the target references changed");
            return (old, targets);
        }

        private ElementId AnnotationRebind(Dictionary<string, object> data)
        {
            var (old, targets) = ReadAnnotationTargets(data);
            var view = _doc.GetElement(old.OwnerViewId) as View ?? throw new InvalidOperationException("the annotation's view does not exist");
            var created = AnnotationRepair.Recreate(old, view, targets);
            var oldId = old.Id;
            var removed = _doc.Delete(oldId);
            if (removed.Count != 1 || !removed.Contains(oldId))
                throw new InvalidOperationException("Revit tried to delete other elements while replacing the annotation");
            _deleted.Add(Ids.ToLong(oldId));
            _r.Deleted++;
            _replacementAnnotations.Add((oldId, created.Id, view.Id, Arg.GetStringList(data, "refs_destino")));
            return created.Id;
        }

        private bool ReplacementAnnotationIsValid((ElementId OldId, ElementId NewId, ElementId ViewId, List<string> References) annotation)
        {
            var live = _doc.GetElement(annotation.NewId);
            if (_doc.GetElement(annotation.OldId) != null || live == null || live.OwnerViewId != annotation.ViewId) return false;
            return AnnotationRepair.References(live).Select(r => r.ConvertToStableRepresentation(_doc)).SequenceEqual(annotation.References);
        }

        private ElementId FilledRegionCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var pts = ((List<object>)t["pts"]).Cast<List<object>>().Select(p => ViewPoint(view, N(p, 0), N(p, 1))).ToList();
            var loop = new CurveLoop();
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) < 1e-6) continue;
                loop.Append(Line.CreateBound(a, b));
            }
            var fr = FilledRegion.Create(_doc, Ids.ToId(WriteKit.L(t, "frtype")), view.Id, new List<CurveLoop> { loop });
            return fr?.Id ?? throw new InvalidOperationException("Revit did not create the region");
        }

        // ---------------- 0.8.0: 2D drafting ----------------

        private void Style(CurveElement ce, Dictionary<string, object> t)
        {
            if (!t.ContainsKey("gs")) return;
            var gs = _doc.GetElement(Ids.ToId(WriteKit.L(t, "gs"))) as GraphicsStyle ?? throw new InvalidOperationException("the line style no longer exists");
            ce.LineStyle = gs;
        }

        private IEnumerable<ElementId> DetailCurvesCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var pts = ((List<object>)t["pts"]).Cast<List<object>>().Select(p => ViewPoint(view, N(p, 0), N(p, 1))).ToList();
            var closed = t.TryGetValue("closed", out var cv) && cv is bool cb && cb;
            var n = closed ? pts.Count : pts.Count - 1;
            var ids = new List<ElementId>();
            for (var i = 0; i < n; i++)
            {
                var dc = _doc.Create.NewDetailCurve(view, Line.CreateBound(pts[i], pts[(i + 1) % pts.Count])) ?? throw new InvalidOperationException("Revit did not create line " + (i + 1));
                Style(dc, t);
                ids.Add(dc.Id);
            }
            return ids;
        }

        private ElementId DetailArcCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var c = (List<object>)t["c"];
            var center = ViewPoint(view, N(c, 0), N(c, 1));
            var right = view is ViewPlan || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend ? XYZ.BasisX : view.RightDirection;
            var up = view is ViewPlan || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend ? XYZ.BasisY : view.UpDirection;
            var arc = Arc.Create(center, WriteKit.D(t, "r"), WriteKit.D(t, "a0") * Math.PI / 180.0, WriteKit.D(t, "a1") * Math.PI / 180.0, right, up);
            var dc = _doc.Create.NewDetailCurve(view, arc) ?? throw new InvalidOperationException("Revit did not create the arc");
            Style(dc, t);
            return dc.Id;
        }

        private ElementId DetailComponentCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var sym = _doc.GetElement(Ids.ToId(WriteKit.L(t, "sym"))) as FamilySymbol ?? throw new InvalidOperationException("the component no longer exists");
            if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
            var at = ViewPoint(view, WriteKit.D(t, "x"), WriteKit.D(t, "y"));
            var fi = _doc.Create.NewFamilyInstance(at, sym, view) ?? throw new InvalidOperationException("Revit did not place the component");
            var rot = WriteKit.D(t, "rot", 0);
            if (Math.Abs(rot) > 1e-9) ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(at, at + view.ViewDirection), rot * Math.PI / 180.0);
            return fi.Id;
        }

        private ElementId DimensionCreate(Dictionary<string, object> t)
        {
            var view = ViewOf(t) ?? throw new InvalidOperationException("the view no longer exists");
            var ra = new ReferenceArray();
            foreach (var r in (List<object>)t["refs"])
            {
                var dc = _doc.GetElement(Ids.ToId(Convert.ToInt64(r, CultureInfo.InvariantCulture))) as DetailCurve ?? throw new InvalidOperationException("line " + r + " no longer exists");
                ra.Append(dc.GeometryCurve.Reference ?? throw new InvalidOperationException("line " + r + " offers no dimensionable reference"));
            }
            var l = (List<object>)t["l"];
            var a = (List<object>)l[0]; var b = (List<object>)l[1];
            var line = Line.CreateBound(ViewPoint(view, N(a, 0), N(a, 1)), ViewPoint(view, N(b, 0), N(b, 1)));
            var dim = t.ContainsKey("dtype")
                ? _doc.Create.NewDimension(view, line, ra, _doc.GetElement(Ids.ToId(WriteKit.L(t, "dtype"))) as DimensionType)
                : _doc.Create.NewDimension(view, line, ra);
            return dim?.Id ?? throw new InvalidOperationException("Revit did not create the dimension (are the lines not parallel to the dimension?)");
        }

        // =====================================================================================================
        // Verification
        // =====================================================================================================

        /// <summary>Re-reads what was touched and compares it with the plan (tolerance 1e-5 ft). Creations are counted per operation.</summary>
        private Dictionary<string, object> Verify()
        {
            int checkedCount = 0, same = 0;
            var diffs = new List<object>();
            foreach (var e in _plan.Entries)
            {
                if (e.Ref > 0 || e.Id == 0 || e.Key == "nuevo" || (e.Key != null && e.Key.Contains("@@"))) continue;
                if (e.Key == "exists") { checkedCount++; same++; continue; }
                if (_deleted.Contains(e.Id)) continue;
                string now;
                try { now = Current(e); } catch (Exception ex) { now = "\u0000" + ex.Message; }
                checkedCount++;
                var want = (e.After ?? "").TrimEnd('*');
                if (PlanEntry.Same(now, want)) { same++; continue; }
                if (diffs.Count < 5)
                    diffs.Add(e.Id + " " + e.Key + ": planned " + Plan.ShowValue(e, want, false) + ", got " + Plan.ShowValue(e, now.StartsWith("\u0000", StringComparison.Ordinal) ? now.Substring(1) : now, false));
            }
            var d = new Dictionary<string, object> { ["releidos"] = (long)checkedCount, ["como_se_planifico"] = (long)same };
            if (checkedCount > same)
            {
                d["distintos"] = (long)(checkedCount - same);
                d["ejemplos"] = diffs;
                d["nota"] = "Revit adjusted those values when applying them (constraints, rounding or the family); review them";
            }
            if (_plan.CreatedByOp.Count > 0)
                d["creados_por_op"] = _plan.CreatedByOp.OrderBy(k => k.Key).ToDictionary(k => "@" + k.Key.ToString(CultureInfo.InvariantCulture), k => (object)(long)k.Value.Count);
            if (_skipped.Count > 0)
                d["omitidos"] = _skipped.ToDictionary(k => k.Key, k => (object)(long)k.Value.Count);
            var mv = VerifyModelCreations();
            if (mv != null) d["verificacion_creacion"] = mv;
            return d;
        }

    }
}
