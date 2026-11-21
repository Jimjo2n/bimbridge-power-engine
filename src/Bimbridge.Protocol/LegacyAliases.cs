// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Bimbridge.Protocol
{
    /// <summary>
    /// LEGACY ALIAS TABLE (protocol layer). Remove after 0.9.5.
    /// Versions up to 0.9.3 spoke a Spanish vocabulary (MCP commands, argument names, operation arguments, enum values, status values,
    /// persisted keys and data folder names). For ONE version the old tokens are still ACCEPTED as input and still READ from disk; they
    /// are normalized here, once, at the entry points (MCP tool arguments, command parsing, request handling, persisted-record readers),
    /// so the rest of the code sees English only. Nothing is ever WRITTEN with a legacy token.
    /// This file and src/Bimbridge.Setup.Core/LegacyMigration.cs are the only places where the legacy names may appear.
    /// </summary>
    public static class LegacyAliases
    {
        /// <summary>Legacy token → English token (keys, verbs, enum-like values, statuses). Generated from the 0.9.4 rename.</summary>
        public static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["abajo"] = "down", ["abierto"] = "open", ["abre_plan"] = "opens_plan", ["abrir"] = "open", ["accion"] = "action",
            ["accion_desconocida"] = "unknown_action", ["aceptacion"] = "acceptance", ["aciertos"] = "hits", ["actividad_"] = "activity_",
            ["activo"] = "active", ["activo_s"] = "active_s", ["actual"] = "current", ["actual_sin_correspondencia"] = "current_without_match",
            ["actualizacion_online"] = "online_update", ["actualizacion_prerelease"] = "update_prerelease",
            ["actualizacion_sin_firma"] = "unsigned_update", ["actualizaciones"] = "updates", ["actualizada"] = "updated", ["actualizar"] = "update",
            ["actualizar_buscar_online"] = "update_check_online", ["actualizar_descargar_online"] = "update_download_online",
            ["actualizar_zip"] = "update_zip", ["actualizar_zip_resultado"] = "update_zip_result", ["acuerdo"] = "agreement",
            ["addin_cargado"] = "addin_loaded", ["addin_grupo_estado"] = "addin_group_status", ["addin_restauracion_ok"] = "addin_restoration_ok",
            ["addin_temporal_ok"] = "addin_temporary_ok", ["adicional"] = "additional", ["adicionales"] = "additional_items",
            ["adimensional"] = "dimensionless", ["adoptar"] = "adopt", ["afin"] = "affine", ["agente"] = "agent", ["agente_fuente"] = "agent_source",
            ["agentes_en_curso"] = "running_agents", ["agua_caliente"] = "hot_water", ["agua_fria"] = "cold_water", ["aislados"] = "isolated",
            ["ajena"] = "foreign", ["ajeno"] = "foreign", ["ajustar"] = "adjust", ["ajustes"] = "settings", ["al_dia"] = "up_to_date",
            ["al_final"] = "at_end", ["al_ras"] = "flush", ["alcance"] = "scope", ["alcance_sin_nivel"] = "scope_without_level",
            ["almacenamiento"] = "storage", ["alta"] = "high", ["alterado"] = "tampered", ["alto"] = "height", ["alto_in"] = "height_in",
            ["alto_mm"] = "height_mm", ["alto_pt"] = "height_pt", ["altura"] = "height", ["altura_incorrecta"] = "wrong_height",
            ["altura_montaje"] = "mounting_height", ["altura_param"] = "height_param", ["altura_texto_in"] = "text_height_in",
            ["alzado"] = "elevation", ["amarillo"] = "yellow", ["ambiguedad"] = "ambiguity", ["ambiguedades"] = "ambiguities",
            ["ambiguo"] = "ambiguous", ["ambiguos"] = "ambiguous_items", ["ambito"] = "scope", ["ambos"] = "both", ["ancho"] = "width",
            ["ancho_in"] = "width_in", ["ancho_mm"] = "width_mm", ["ancho_pt"] = "width_pt", ["ancla"] = "anchor",
            ["ancla_invalida"] = "invalid_anchor", ["anclas"] = "anchors", ["anclas_insuficientes"] = "insufficient_anchors", ["anfitrion"] = "host",
            ["anfitrion_supervisados"] = "supervised_hosts", ["angulo"] = "angle", ["angulo_deg"] = "angle_deg", ["anidados"] = "nested",
            ["anotacion"] = "annotation", ["anotaciones"] = "annotations", ["anotaciones_audit"] = "annotations_audit", ["anotar"] = "note",
            ["anterior"] = "previous", ["anterior_sin_correspondencia"] = "previous_without_match", ["antes"] = "before",
            ["antes_bloqueo"] = "before_lock", ["antes_confirmar"] = "before_commit", ["antes_escribir"] = "before_write", ["apagado"] = "off",
            ["apagado_pedido"] = "shutdown_requested", ["apagar"] = "shutdown", ["apartado"] = "set_aside", ["apertura"] = "opening",
            ["aplicables"] = "applicable", ["aplicada"] = "applied", ["aplicada_con_diferencias"] = "applied_with_differences",
            ["aplicada_verificada"] = "applied_verified", ["aplicado"] = "applied", ["aplicado_error_resultado"] = "applied_result_error",
            ["aplicado_verificado"] = "applied_verified", ["aplicar"] = "apply", ["aplicar_cola"] = "apply_queue",
            ["aplicar_verificar_ms"] = "apply_verify_ms", ["aprobada"] = "approved", ["aprobada_por"] = "approved_by",
            ["aprobada_por_mi"] = "approved_by_me", ["aprobado"] = "approved", ["archivada"] = "archived", ["archivo"] = "file",
            ["archivo_escritura"] = "write_file", ["archivo_exportado"] = "file_exported", ["archivos"] = "files",
            ["archivos_danados"] = "damaged_files", ["aristas"] = "edges", ["aristas_sin_nodo"] = "edges_without_node", ["arnes"] = "harness",
            ["arriba"] = "up", ["artefacto_verificado"] = "artifact_verified", ["artefactos"] = "artifacts",
            ["asignado_bytes_mediana"] = "allocated_bytes_median", ["asociado_a"] = "associated_to", ["asociados"] = "associated",
            ["asociar"] = "associate", ["auditoria"] = "audit", ["auditoria_append"] = "audit_append",
            ["auditoria_bytes_por_registro"] = "audit_bytes_per_record", ["auditoria_incompleta"] = "incomplete_audit",
            ["auditoria_intencion_mas_resultado"] = "audit_intent_plus_result", ["auditoria_no_disponible"] = "audit_unavailable",
            ["auditoria_verify_completo"] = "audit_verify_full", ["ausentes"] = "missing", ["automatico"] = "automatic", ["autor"] = "author",
            ["autoridad"] = "authority", ["autoriza"] = "authorizes", ["autorizacion"] = "authorization",
            ["autorizacion_derivada"] = "derived_authorization", ["autorizacion_operador_requerida"] = "operator_authorization_required",
            ["autorizacion_requerida"] = "authorization_required", ["autorizaciones"] = "authorizations", ["aviso"] = "warning",
            ["aviso_conjunto"] = "set_warning", ["aviso_consulta"] = "query_warning", ["aviso_intento"] = "attempt_warning",
            ["aviso_inventario"] = "inventory_warning", ["aviso_limite"] = "limit_warning", ["aviso_modelo"] = "model_warning",
            ["aviso_resultado"] = "result_warning", ["aviso_texto"] = "text_warning", ["aviso_vista"] = "view_warning", ["avisos"] = "warnings",
            ["avisos_historial"] = "history_warnings", ["avisos_revit"] = "revit_warnings", ["ayuda"] = "help", ["azul"] = "blue", ["baja"] = "low",
            ["bajo"] = "low", ["bandas"] = "bands", ["bandeja"] = "tray", ["barrera"] = "barrier", ["barrera_en"] = "barrier_at",
            ["barreras"] = "barriers", ["barreras_ok"] = "barriers_ok", ["basura"] = "garbage", ["bidireccional"] = "bidirectional",
            ["bloqueada"] = "blocked", ["bloqueado"] = "blocked", ["bloquear_inyeccion_csharp"] = "block_csharp_injection", ["bloqueo"] = "lock",
            ["bloques"] = "blocks", ["booleana"] = "boolean_op", ["booleana_completa"] = "boolean_complete", ["booleana_fallida"] = "boolean_failed",
            ["booleanas"] = "booleans", ["borrado"] = "deleted", ["borrados"] = "deleted_items", ["borrados_intentados"] = "attempted_deletes",
            ["borrados_que_persisten"] = "deleted_persisting", ["borrar"] = "delete", ["borrardatos"] = "removedata",
            ["bridge_no_instalado"] = "bridge_not_installed", ["bridge_sin_herramientas"] = "bridge_no_tools",
            ["bridge_sin_respuesta"] = "bridge_no_response", ["builds_distintos"] = "different_builds", ["buscar"] = "search",
            ["busqueda_mas_ficha_chars"] = "search_plus_card_chars", ["bytes_ensamblados_estimados"] = "estimated_assembly_bytes",
            ["bytes_estimados"] = "estimated_bytes", ["bytes_imagen_media"] = "mean_image_bytes", ["bytes_peticion"] = "request_bytes",
            ["bytes_respuesta"] = "response_bytes", ["cabeceras"] = "headers", ["cad_importado"] = "imported_cad", ["cad_vinculado"] = "linked_cad",
            ["cadena_nueva"] = "new_chain", ["caida_en"] = "crash_at", ["caja"] = "box", ["caja_seccion"] = "section_box",
            ["cajetin"] = "titleblock", ["calibracion"] = "calibration", ["calibrar"] = "calibrate", ["caliente"] = "hot", ["cambiado"] = "changed",
            ["cambiados"] = "changed", ["cambios"] = "changes", ["cambios_desde_el_preflight"] = "changes_since_preflight",
            ["cambios_error"] = "error_changes", ["cambios_guardados"] = "changes_saved", ["cambios_intentados"] = "attempted_changes",
            ["cambios_ultima_operacion_bytes"] = "last_operation_changes_bytes", ["campana"] = "campaign", ["campana080"] = "campaign080",
            ["campo"] = "field", ["campos"] = "fields", ["campos_no_confirmados"] = "unconfirmed_fields", ["cancelacion"] = "cancellation",
            ["cancelacion_en_curso"] = "cancellation_running", ["cancelacion_solicitada"] = "cancellation_requested", ["cancelada"] = "cancelled",
            ["cancelada_tiempo"] = "cancelled_timeout", ["cancelada_usuario"] = "cancelled_by_user", ["canceladas_en_cola"] = "cancelled_in_queue",
            ["cancelado"] = "cancelled", ["cancelados"] = "cancelled_items", ["cancelar"] = "cancel",
            ["candidato_descartado"] = "discarded_candidate", ["candidatos"] = "candidates", ["cantidad"] = "quantity",
            ["cantidad_esperada"] = "expected_count", ["cantidad_paginas"] = "page_count", ["cantidades"] = "quantities",
            ["cantidades_base"] = "base_quantities", ["capacidad"] = "capability", ["capacidades"] = "capabilities",
            ["capacidades_cols"] = "capabilities_cols", ["capacidades_usadas"] = "capabilities_used", ["capas"] = "layers", ["captura"] = "capture",
            ["capturas"] = "captures", ["cara"] = "face", ["cara_lado"] = "face_side", ["cara_x"] = "face_x", ["cara_y"] = "face_y",
            ["cara_z"] = "face_z", ["caras"] = "faces", ["caras_planas"] = "planar_faces", ["carga_ms"] = "load_ms", ["cargada_en"] = "loaded_in",
            ["cargado"] = "loaded", ["cargar"] = "load", ["cargar_archivo"] = "load_file", ["carpeta"] = "folder",
            ["carpeta_usuario"] = "user_folder", ["carpetas"] = "folders", ["carpetas_actualizacion"] = "update_folders",
            ["carpetas_documentos"] = "document_folders", ["carpetas_familias"] = "family_folders", ["carpetas_laboratorio"] = "lab_folders",
            ["casi_duplicado"] = "near_duplicate", ["casi_duplicado_de"] = "near_duplicate_of", ["casi_duplicados"] = "near_duplicates",
            ["catalogo"] = "catalog", ["catalogo_completo_chars"] = "full_catalog_chars", ["categoria"] = "category",
            ["categoria_desconocida"] = "unknown_category", ["categoria_familia"] = "family_category", ["categoria_oculta"] = "hidden_category",
            ["categorias"] = "categories", ["categorias_top"] = "top_categories", ["categorías"] = "categories", ["catálogo"] = "catalog",
            ["caudal_tuberia"] = "pipe_flow", ["causas"] = "causes", ["celda"] = "cell", ["celda_fuera_de_rango"] = "cell_out_of_range",
            ["celdas"] = "cells", ["celdas_esperadas"] = "expected_cells", ["centro"] = "center", ["centro_in"] = "center_in",
            ["centro_mm"] = "center_mm", ["centro_norm"] = "center_norm", ["centro_pt"] = "center_pt", ["centro_x"] = "center_x",
            ["centro_y"] = "center_y", ["cerca"] = "near", ["cerca_sin_conectar"] = "near_unconnected", ["cerrada"] = "closed",
            ["cerrado"] = "closed", ["cerrar"] = "close", ["chars_agente"] = "agent_chars", ["chars_entrada"] = "chars_in",
            ["chars_salida"] = "chars_out", ["checkpoint_a_medias"] = "checkpoint_half_written", ["checkpoint_adelantado"] = "checkpoint_ahead",
            ["checkpoint_alterado"] = "checkpoint_tampered", ["checkpoint_atrasado"] = "checkpoint_behind",
            ["checkpoint_contradice_diario"] = "checkpoint_contradicts_journal", ["checkpoint_de_otro_plan"] = "checkpoint_other_plan",
            ["checkpoint_esquema"] = "checkpoint_schema", ["checkpoint_ilegible"] = "checkpoint_unreadable",
            ["checkpoint_invalido"] = "checkpoint_invalid", ["checkpoint_otra_historia"] = "checkpoint_other_history", ["choque"] = "clash",
            ["choque_no_evaluable"] = "clash_not_evaluable", ["choque_real"] = "real_clash", ["choques"] = "clashes", ["cian"] = "cyan",
            ["cielos"] = "ceilings", ["cierre_ms"] = "close_ms", ["cifrado"] = "encrypted", ["circuito"] = "circuit", ["circulo"] = "circle",
            ["cita"] = "quote", ["clase"] = "class", ["clase_esperada"] = "expected_class", ["clasificacion"] = "classification", ["clave"] = "key",
            ["clave_conector"] = "connector_key", ["clave_desconocida"] = "unknown_key", ["clave_doc"] = "doc_key",
            ["clave_idempotencia"] = "idempotency_key", ["clave_intento"] = "attempt_key", ["clave_vinculada"] = "linked_key", ["claves"] = "keys",
            ["claves_actualizacion"] = "update_keys", ["cliente"] = "client", ["cliente_sin_registro"] = "client_unregistered",
            ["clientes"] = "clients", ["cm_antes_"] = "cm_before_", ["cobertura"] = "coverage", ["codigo"] = "code",
            ["codigo_autorizado"] = "authorized_code", ["codigo_confiable_requerido"] = "trusted_code_required",
            ["codigo_confirmado"] = "committed_code", ["codigo_dinamico"] = "dynamic_code", ["codigo_param"] = "code_param", ["codo"] = "elbow",
            ["codos"] = "elbows", ["coherente"] = "coherent", ["coincide"] = "matches", ["coincide_con_instalada"] = "matches_installed",
            ["coinciden"] = "match", ["cola"] = "queue", ["cola_incompleta_apartada"] = "incomplete_tail_set_aside",
            ["cola_inconsistente"] = "inconsistent_queue", ["cola_llena"] = "queue_full", ["cola_maxima_vista"] = "max_queue_seen",
            ["cola_retenida"] = "held_queue", ["coleccionable_bytes_retenidos_tras_descargar"] = "collectible_bytes_retained_after_unload",
            ["coleccionable_descargado"] = "collectible_unloaded", ["colision"] = "collision", ["colisiones"] = "collisions",
            ["colocacion"] = "placement", ["colocacion_"] = "placement_", ["colocacion_72"] = "placement_72", ["colocar"] = "place",
            ["colorear"] = "color_by", ["cols_muestra"] = "sample_cols", ["columna"] = "column", ["columnas"] = "columns",
            ["columnas_cuerpo"] = "body_columns", ["comando_activo"] = "active_command", ["comando_iniciado"] = "command_started",
            ["comandos_cinta"] = "ribbon_commands", ["combinado"] = "combined", ["combinar"] = "combine", ["comentario"] = "comment",
            ["comer"] = "eat", ["comer_y_fallar"] = "eat_and_fail", ["commit_confirmado"] = "commit_confirmed",
            ["commit_ejecutado"] = "commit_executed", ["commit_ensayo_rechazado_por_guardia"] = "trial_commit_rejected_by_guard", ["como"] = "as",
            ["como_se_pidio"] = "as_requested", ["como_se_planifico"] = "as_planned", ["compactar"] = "compact", ["compacto"] = "compact_profile",
            ["compartidas"] = "shared_items", ["compatibilidad"] = "compatibility", ["compilacion_csharp"] = "csharp_compilation",
            ["compilacion_ms_total"] = "compile_ms_total", ["compilador"] = "compiler", ["compilar_2026"] = "compile_2026",
            ["compilar_ms"] = "compile_ms", ["completa"] = "full", ["completado_con_diferencias"] = "completed_with_differences",
            ["completo"] = "complete", ["componente"] = "component", ["componentes"] = "components", ["componentes_diagrama"] = "diagram_components",
            ["componentes_fisicos"] = "physical_components", ["comprobaciones"] = "checks", ["comprobado"] = "checked", ["comprobados"] = "checked",
            ["comunes"] = "common", ["con_a"] = "with_a", ["con_b"] = "with_b", ["con_cuarto"] = "with_room", ["con_desviacion"] = "with_deviation",
            ["con_diferencias"] = "with_differences", ["con_errores"] = "with_errors", ["con_leader"] = "with_leader", ["con_valor"] = "with_value",
            ["concesion"] = "lease", ["concesion_comprobar_10"] = "lease_check_10", ["conectada"] = "connected", ["conectado_a"] = "connected_to",
            ["conectado_con_hueco"] = "connected_with_gap", ["conectado_fuera_del_conjunto"] = "connected_outside_set", ["conectar"] = "connect",
            ["conector"] = "connector", ["conectores"] = "connectors", ["conectores_desconectados"] = "disconnected_connectors",
            ["conectores_instancia"] = "instance_connectors", ["conectores_libres"] = "free_connectors",
            ["conectores_revisados"] = "reviewed_connectors", ["conectores_rfa"] = "rfa_connectors", ["conexion_cerrada"] = "connection_closed",
            ["conexion_fisica"] = "physical_connection", ["confianza"] = "confidence", ["confianza_baja"] = "low_confidence",
            ["configuracion"] = "configuration", ["confirma"] = "confirms", ["confirmada"] = "committed", ["confirmadas"] = "committed_items",
            ["confirmado"] = "committed", ["confirmado_por"] = "confirmed_by", ["confirmados"] = "committed", ["conflicto"] = "conflict",
            ["conflicto_existente"] = "existing_conflict", ["conflictos"] = "conflicts", ["conforme"] = "conforming", ["conformes"] = "conforming",
            ["congeladas"] = "frozen_items", ["conjunto"] = "sheet_set", ["conjunto_antes"] = "set_before",
            ["conjunto_para_regenerar"] = "set_to_regenerate", ["conjuntos_guardados"] = "saved_sets", ["conservado"] = "kept",
            ["consistencia_geometrica"] = "geometric_consistency", ["construccion"] = "construction",
            ["construccion_derivada_de"] = "construction_derived_from", ["consulta"] = "query", ["consulta_ms_mediana"] = "query_ms_median",
            ["consulta_ns_por_punto"] = "query_ns_per_point", ["consulta_verificada"] = "verified_query", ["consultar"] = "query_check",
            ["contacto"] = "contact", ["contactos"] = "contacts", ["contar"] = "count", ["contencion_no_evaluable"] = "containment_not_evaluable",
            ["contenido"] = "content", ["contenido_sospechoso"] = "suspicious_content", ["contenido_vinculos"] = "links_content",
            ["contiene"] = "contains", ["continuar"] = "continue", ["continuidad"] = "continuity", ["continuidad_fisica"] = "physical_continuity",
            ["contra"] = "against", ["contraste"] = "contrast", ["controla"] = "controls", ["convergente"] = "convergent",
            ["conversacion"] = "conversation", ["coordenadas"] = "coordinates", ["copia"] = "copy", ["copia_del_plan"] = "plan_copy",
            ["copia_iniciada"] = "copy_started", ["copia_observada"] = "copy_observed", ["copiar"] = "copy", ["correctos"] = "correct",
            ["correlacion"] = "correlation", ["correspondencia"] = "correspondence",
            ["correspondencia_individual_origen"] = "individual_origin_correspondence", ["cortado"] = "cut", ["corte"] = "cut",
            ["coste_fijo_ms"] = "fixed_cost_ms", ["cota"] = "dimension", ["cota_mm"] = "dimension_mm", ["cotas"] = "dimensions",
            ["creaciones_esperadas"] = "expected_creations", ["creaciones_verificadas"] = "verified_creations", ["creada"] = "created",
            ["creado"] = "created", ["creado_utc"] = "created_utc", ["creados"] = "created", ["creados_por_op"] = "created_by_op",
            ["creados_por_tipo"] = "created_by_type", ["creados_que_persisten"] = "created_persisting",
            ["creados_sin_observar"] = "created_unobserved", ["creados_temporales"] = "temporary_created", ["crear"] = "create",
            ["cruces"] = "crossings", ["csharp_dinamico"] = "dynamic_csharp", ["cuando"] = "when", ["cuarentena"] = "quarantine",
            ["cuarto"] = "room", ["cuartos"] = "rooms", ["cubiertas"] = "roofs", ["cuerpo"] = "body", ["cuerpo_mm"] = "body_mm",
            ["cumple_regla"] = "meets_rule", ["cumplidas"] = "met", ["cursor_no_disponible"] = "cursor_unavailable", ["curva"] = "curve",
            ["curvas"] = "curves", ["código"] = "code", ["dato"] = "data_type", ["datos"] = "data", ["datos_sin_fuente"] = "data_without_source",
            ["debajo"] = "below", ["decision_profesional"] = "professional_decision", ["decisiones"] = "decisions", ["declarada"] = "declared",
            ["definicion"] = "definition", ["degenerado"] = "degenerate", ["dejar"] = "leave", ["demasiado_grande"] = "too_large",
            ["demostracion"] = "demonstration", ["denegado"] = "denied", ["dentro"] = "inside", ["depende_de"] = "depends_on",
            ["depende_del_modelo"] = "depends_on_model", ["dependencia"] = "dependency", ["dependiente"] = "dependent", ["der"] = "right_side",
            ["derecha"] = "right", ["derivada_de"] = "derived_from", ["desactualizada"] = "outdated", ["desague"] = "drain",
            ["descartadas"] = "discarded", ["descartado"] = "discarded", ["descartar"] = "discard", ["descartar_cola"] = "discard_queue",
            ["desconectado"] = "disconnected", ["desconectar"] = "disconnect", ["desconectivo_hvac"] = "hvac_disconnect",
            ["desconocida"] = "unknown", ["desconocido"] = "unknown", ["desconocido_requiere_reconciliacion"] = "unknown_requires_reconcile",
            ["descontar_tipo_param"] = "subtract_type_param", ["descripcion"] = "description", ["descubrimiento"] = "discovery", ["desde"] = "from",
            ["desde_cuarentena"] = "from_quarantine", ["desde_minima"] = "min_from", ["desde_seq"] = "from_seq",
            ["deserializar_ms"] = "deserialize_ms", ["desfase"] = "offset", ["desfase_mm"] = "offset_mm", ["desinstalar"] = "uninstall",
            ["despacho"] = "dispatch", ["despacho_total_ms"] = "dispatch_total_ms", ["desplazado"] = "displaced", ["desplazado_mm"] = "displaced_mm",
            ["despues"] = "after", ["desregistrada"] = "unregistered", ["desregistrar"] = "unregister", ["destino"] = "target",
            ["destino_no_disponible"] = "target_unavailable", ["desviacion_operador"] = "operator_deviation", ["desviaciones"] = "deviations",
            ["detalle"] = "detail", ["detalle_paginas"] = "pages_detail", ["detalles"] = "with_detailing", ["diagnostico"] = "diagnostics",
            ["diagnóstico"] = "diagnostics", ["diagrama"] = "diagram", ["dialogo_mapeo"] = "mapping_dialog", ["diametro"] = "diameter",
            ["diametro_mm"] = "diameter_mm", ["diario"] = "journal", ["diario_ausente"] = "journal_missing",
            ["diario_de_otro_plan"] = "journal_other_plan", ["diario_esquema_no_soportado"] = "journal_schema_not_supported",
            ["diario_hash_invalido"] = "journal_invalid_hash", ["diario_linea_final_truncada"] = "journal_truncated_last_line",
            ["diario_lineas"] = "journal_lines", ["diario_no_disponible"] = "journal_unavailable",
            ["diario_sin_apertura"] = "journal_without_opening", ["dibujo"] = "drafting", ["diferencia_"] = "difference_",
            ["diferencias"] = "differences", ["dimensiones"] = "dimensions_list", ["direccion"] = "direction", ["direccional"] = "directional",
            ["directiva"] = "directive", ["disco_sha_es_el_guardado"] = "disk_sha_is_saved", ["disco_valores_esperados"] = "disk_expected_values",
            ["discrepancia"] = "discrepancy", ["discrepancias"] = "discrepancies", ["disponible"] = "available", ["dispositivo"] = "device",
            ["distancia"] = "distance", ["distancia_ft"] = "distance_ft", ["distintas"] = "different", ["distinto"] = "different",
            ["distintos"] = "different_items", ["doble"] = "double", ["doc_explicito"] = "explicit_doc", ["documentacion"] = "documentation",
            ["documento"] = "document", ["documento_bloqueado"] = "document_locked",
            ["documento_bloqueado_reconciliacion"] = "document_locked_reconciliation",
            ["documento_con_cambios_sin_guardar"] = "document_with_unsaved_changes", ["documento_distinto"] = "different_document",
            ["documento_ocupado"] = "document_busy", ["documento_sin_exclusion"] = "document_without_exclusion", ["documentos"] = "documents",
            ["dominio"] = "domain", ["dos"] = "two", ["dos_commits_exactos"] = "exactly_two_commits", ["ducto"] = "duct", ["dueno"] = "owner",
            ["duplicada"] = "duplicated", ["duplicado"] = "duplicate", ["duplicado_semantico"] = "semantic_duplicate", ["duplicados"] = "duplicates",
            ["duplicar"] = "duplicate", ["durante_escritura"] = "during_write", ["eco"] = "echo", ["efecto"] = "effect",
            ["efecto_constructor"] = "constructor_effect", ["efecto_incierto"] = "uncertain_effect", ["efecto_parcial"] = "partial_effect",
            ["efectos_no_revertidos"] = "unreverted_effects", ["eje"] = "axis", ["ejecucion"] = "execution", ["ejecucion_fin"] = "execution_end",
            ["ejecucion_inicio"] = "execution_start", ["ejecucion_ms_en_proceso"] = "execution_ms_in_process", ["ejecutable"] = "executable",
            ["ejecutada"] = "executed", ["ejecutando"] = "running_now", ["ejecutando_al_parar"] = "running_at_stop", ["ejecutar"] = "run",
            ["ejecutor"] = "executor", ["ejecutor_ui"] = "ui_executor", ["ejemplo"] = "example", ["ejemplos"] = "examples", ["ejes"] = "grids",
            ["electrico"] = "electrical", ["elemento"] = "element", ["elemento_id"] = "element_id", ["elementos"] = "elements",
            ["elementos_nuevos_desde_entonces"] = "new_elements_since", ["elementos_revisados"] = "elements_reviewed",
            ["elementos_sobrantes"] = "leftover_elements", ["emparejados"] = "paired", ["empezo"] = "started", ["empotrada"] = "embedded",
            ["empotrado"] = "embedded", ["en_cache"] = "cached", ["en_cadena_apartada"] = "in_set_aside_chain", ["en_cola"] = "queued",
            ["en_colector_de_vista"] = "in_view_collector", ["en_curso"] = "running", ["en_ejecucion"] = "executing", ["en_error"] = "on_error",
            ["en_modelo"] = "in_model", ["en_proceso"] = "in_progress", ["en_region"] = "in_region", ["en_sitio"] = "in_place",
            ["en_uso"] = "in_use", ["encima"] = "above", ["encuadrados"] = "framed", ["encuadre"] = "framing", ["ensayada"] = "trialed",
            ["ensayado"] = "trialed", ["ensayo"] = "trial", ["ensayo_correcto_revertido"] = "trial_ok_reverted",
            ["ensayo_revertido"] = "trial_reverted", ["entero"] = "integer", ["entorno"] = "environment", ["entrada"] = "in",
            ["entrada_bytes"] = "input_bytes", ["entradas"] = "inputs", ["entrega"] = "delivery", ["entrega_confirmada"] = "delivery_confirmed",
            ["enumerar"] = "enumerate", ["enviar"] = "send", ["envio_confirmado"] = "send_confirmed", ["equipo"] = "machine",
            ["equipos"] = "equipment", ["equipos_audit"] = "equipment_audit", ["error_preparar"] = "prepare_error", ["errores"] = "errors",
            ["errores_compilacion"] = "compile_errors", ["errores_plan"] = "plan_errors", ["errores_resultado"] = "result_errors",
            ["escala"] = "scale", ["escala_incoherente"] = "inconsistent_scale", ["escala_revit"] = "revit_scale", ["escaleras"] = "stairs",
            ["escaneado"] = "scanned", ["escenario"] = "scenario", ["escribir"] = "write", ["escrito_utc"] = "written_utc",
            ["escritor_obsoleto"] = "stale_writer", ["escritura"] = "write", ["escritura_en_fase_de_lectura"] = "write_in_read_phase",
            ["escritura_modelo"] = "model_write", ["escrituras_no_permitidas"] = "writes_not_allowed", ["espacio"] = "space",
            ["espacios"] = "spaces", ["especificacion"] = "specification", ["espejadas"] = "mirrored", ["espejo"] = "mirrored", ["espera"] = "wait",
            ["espera_cola_ms"] = "queue_wait_ms", ["espera_s"] = "wait_s", ["esperado"] = "expected", ["esperado_comprobado"] = "expected_checked",
            ["esperado_dibujo"] = "expected_drawing", ["esperado_erroneo"] = "wrong_expected", ["esperado_ft"] = "expected_ft",
            ["esperados"] = "expected_items", ["esperando_cierre_revit"] = "waiting_for_revit_close", ["esperando_contexto"] = "waiting_for_context",
            ["esperar"] = "wait", ["esperas"] = "waits", ["esperas_compartidas"] = "shared_waits", ["esquema"] = "schema", ["esquinas"] = "corners",
            ["estable"] = "stable", ["estado"] = "status", ["estado_alli"] = "status_there", ["estado_anterior"] = "previous_status",
            ["estado_completo_bytes"] = "full_status_bytes", ["estado_corrupto"] = "corrupt_status", ["estado_desconocido"] = "unknown_status",
            ["estado_directiva"] = "directive_status", ["estado_final"] = "final_status", ["estado_fuera_alcance"] = "out_of_scope_status",
            ["estado_inicial"] = "initial_status", ["estado_job"] = "job_status", ["estado_observado"] = "observed_status",
            ["estado_op"] = "op_status", ["estado_op_ensayada"] = "trialed_op_status", ["estado_op_verificada"] = "verified_op_status",
            ["estado_plan"] = "plan_status", ["estado_recibo"] = "receipt_status", ["estado_sin_autorizacion"] = "status_without_authorization",
            ["estado_tras_caida"] = "status_after_crash", ["estados"] = "statuses", ["estilo"] = "style", ["estimadas"] = "estimated",
            ["estrategia"] = "strategy", ["estructura"] = "structure", ["estructural"] = "structural", ["etiqueta"] = "tag",
            ["etiqueta_duplicada"] = "duplicate_tag", ["etiqueta_sobra"] = "extra_tag", ["etiquetar_luces"] = "tag_lights",
            ["etiquetas"] = "tags_list", ["evento_desconocido"] = "unknown_event", ["evento_revit"] = "revit_event", ["evidencia"] = "evidence",
            ["evidencia_addin"] = "addin_evidence", ["evidencia_insuficiente"] = "insufficient_evidence", ["evidencia_pdf"] = "pdf_evidence",
            ["evidencia_requerida"] = "evidence_required", ["evidencia_revision"] = "evidence_revision", ["exacta"] = "exact", ["exacto"] = "exact",
            ["excepcion"] = "exception", ["excepciones"] = "exceptions", ["excepciones_bytes"] = "exceptions_bytes",
            ["excepciones_justificadas"] = "justified_exceptions", ["excluido"] = "excluded", ["excluidos"] = "excluded_items",
            ["excluir"] = "exclude", ["exec_codigo"] = "exec_code", ["exec_ensayo"] = "exec_trial",
            ["exec_ensayo_rechazado_por_guardia"] = "exec_trial_rejected_by_guard", ["exigir_conexion"] = "require_connection",
            ["existe"] = "exists", ["existente_compartido"] = "shared_existing", ["existente_id"] = "existing_id", ["existentes"] = "existing",
            ["expectativas"] = "expectations", ["exportar"] = "export", ["exportar_laminas"] = "export_sheets", ["exportes"] = "exports",
            ["expresión"] = "expression", ["externo"] = "external", ["extraccion"] = "extraction",
            ["extraccion_estructural"] = "structural_extraction", ["extraccion_fallida"] = "extraction_failed",
            ["extraccion_textual"] = "text_extraction", ["extraible"] = "extractable", ["extremo"] = "end", ["extremos"] = "ends",
            ["extremos_abiertos"] = "open_ends", ["extremos_distintos"] = "different_ends", ["factor_revit"] = "revit_factor", ["falla"] = "fail",
            ["fallida"] = "failed", ["fallidas"] = "failed", ["fallido"] = "failed", ["fallidos"] = "failed_items", ["fallo"] = "failure",
            ["fallo_funcional_revertido"] = "functional_failure_reverted", ["fallo_reversion"] = "rollback_failure",
            ["fallo_sin_reversion"] = "failure_without_rollback", ["fallos"] = "misses", ["fallos_cache"] = "cache_misses_count",
            ["fallos_geometria"] = "geometry_failures", ["fallos_verificacion_bytes"] = "verification_failures_bytes", ["falso"] = "false",
            ["faltan"] = "missing_items", ["faltante"] = "missing", ["faltantes"] = "missing", ["familia"] = "family", ["familias"] = "families",
            ["fase"] = "phase", ["fecha_utc"] = "date_utc", ["fencing_regresivo"] = "fencing_regression", ["fijar_parametro"] = "set_parameter",
            ["fijar_parametro_lote"] = "set_parameter_batch", ["fijar_verificado_tras_lote"] = "set_verified_after_batch", ["fila"] = "row",
            ["fila_encabezado"] = "header_row", ["fila_final"] = "end_row", ["fila_inicio"] = "start_row", ["filas"] = "rows",
            ["filas_cuerpo"] = "body_rows", ["filas_omitidas"] = "skipped_rows", ["filtro"] = "filter", ["filtro_activo"] = "active_filter",
            ["filtro_id"] = "filter_id", ["filtro_visible"] = "visible_filter", ["filtros"] = "filters",
            ["filtros_coincidentes"] = "matching_filters", ["filtros_esperados"] = "expected_filters", ["fin"] = "end", ["fin_abierto"] = "open_end",
            ["finalizar_observado"] = "finish_observed", ["fino"] = "fine", ["firma"] = "signature", ["firma_exigida"] = "signature_required",
            ["firma_invalida"] = "invalid_signature", ["firma_miembros"] = "member_signature", ["firmado"] = "signed", ["fisico"] = "physical",
            ["flotando"] = "floating", ["flujo"] = "workflow", ["flujos"] = "workflows", ["fondo"] = "bottom", ["forja"] = "forge",
            ["forma"] = "shape", ["forma_bbox"] = "shape_bbox", ["formato_digest"] = "digest_format", ["formato_res"] = "result_format",
            ["frente"] = "facing", ["fria"] = "cold", ["fuente"] = "source", ["fuente_alterada"] = "source_tampered",
            ["fuente_aplicada"] = "applied_source", ["fuente_bbox"] = "source_bbox", ["fuente_construccion"] = "source_construction",
            ["fuente_construccion_sha256"] = "source_construction_sha256", ["fuente_derivada_de"] = "source_derived_from",
            ["fuente_id"] = "source_id", ["fuente_identidad"] = "identity_source", ["fuente_revision"] = "source_revision",
            ["fuente_sha256"] = "source_sha256", ["fuente_tipo"] = "source_type_kind", ["fuentes"] = "sources",
            ["fuentes_en_conflicto"] = "conflicting_sources", ["fuera"] = "outside", ["fuera_de_losa"] = "off_slab",
            ["fuera_de_recorte"] = "outside_crop", ["funcion"] = "function", ["generacion"] = "generation", ["genericos"] = "generic",
            ["geometria"] = "geometry", ["geometria_ms_mediana"] = "geometry_ms_median", ["geometría"] = "geometry",
            ["girado_grados"] = "rotated_degrees", ["giro"] = "rotation", ["giro_deg"] = "rotation_deg", ["giro_incorrecto"] = "wrong_rotation",
            ["giro_pt"] = "rotation_pt", ["globo_pt"] = "bubble_pt", ["grado"] = "degree", ["graficos"] = "graphics",
            ["graficos_elemento"] = "element_graphics", ["grafo"] = "graph", ["gravedad"] = "severity", ["gris"] = "gray",
            ["grosor"] = "line_weight", ["grupo"] = "group", ["grupo_estado"] = "group_status",
            ["grupo_rolledback_literal"] = "group_rolledback_literal", ["grupos"] = "groups", ["grupos_audit"] = "groups_audit",
            ["grupos_duplicados"] = "duplicate_groups", ["guardadas"] = "saved_count", ["guardado"] = "saved",
            ["guardado_confirmado"] = "save_confirmed", ["guardado_ok"] = "save_ok", ["guardado_por_generacion"] = "save_by_generation",
            ["guardados"] = "saved_items", ["guardar"] = "save", ["guardar_al_final"] = "save_at_end", ["guardar_antes"] = "save_before",
            ["guardar_cada"] = "save_every", ["guardar_como"] = "save_as", ["hallazgos"] = "findings", ["hashes_no_coinciden"] = "hashes_mismatch",
            ["hasta"] = "until", ["hasta_seq"] = "to_seq", ["hecho"] = "done", ["herramienta"] = "tool_name",
            ["herramientas_no_expuestas_al_agente"] = "tools_not_exposed_to_agent", ["herramientas_no_permitidas"] = "tools_not_allowed",
            ["hilo_revit"] = "revit_thread", ["hilo_revit_ms"] = "revit_thread_ms", ["hilos"] = "threads", ["historial"] = "history",
            ["historial_compartido"] = "shared_history", ["hito"] = "milestone", ["hitos"] = "milestones", ["hoja"] = "sheet",
            ["hoja_ref"] = "sheet_ref", ["hojas"] = "sheets", ["hola"] = "hello", ["holgura"] = "clearance", ["holgura_in"] = "clearance_in",
            ["holgura_max_in"] = "max_clearance_in", ["holgura_max_mm"] = "max_clearance_mm", ["holgura_mm"] = "clearance_mm", ["hueco"] = "gap",
            ["hueco_ft"] = "gap_ft", ["huella_central"] = "central_fingerprint", ["huella_disco"] = "disk_fingerprint",
            ["huella_doc"] = "doc_fingerprint", ["huella_identica"] = "identical_fingerprint", ["huella_operaciones"] = "operations_fingerprint",
            ["huella_origen"] = "origin_fingerprint", ["huella_original"] = "original_fingerprint", ["huella_plan"] = "plan_fingerprint",
            ["id_colocacion"] = "placement_id", ["id_no_determinista"] = "nondeterministic_id", ["id_simbolo"] = "symbol_id",
            ["idempotencia"] = "idempotency", ["identidad"] = "identity", ["identidad_fuente"] = "identity_source",
            ["identidad_insuficiente"] = "insufficient_identity", ["identidad_obligatoria"] = "identity_required", ["ids_borrados"] = "deleted_ids",
            ["ids_creados"] = "created_ids", ["ids_esperados"] = "expected_ids", ["ids_no_previstos"] = "unexpected_ids",
            ["ids_observados"] = "observed_ids", ["ids_repetidos_invalidos"] = "repeated_invalid_ids",
            ["ids_repetidos_validos"] = "repeated_valid_ids", ["ignorar"] = "ignore", ["igual"] = "equal", ["igual_a"] = "equal_to",
            ["igual_al_anterior"] = "same_as_previous", ["ilegal"] = "illegal", ["ilimitado"] = "unlimited", ["imagen"] = "image",
            ["implementacion"] = "implementation", ["inactividad"] = "inactivity", ["inactivo"] = "inactive", ["inciertas"] = "uncertain_items",
            ["incierto"] = "uncertain", ["incierto_"] = "uncertain_", ["incluido"] = "included", ["incluir"] = "include",
            ["incluir_rejillas"] = "include_grids", ["incompatibles"] = "incompatible_items", ["independencia"] = "independence",
            ["independencia_no_comprobable"] = "independence_not_checkable", ["indexadas"] = "indexed", ["indice"] = "index",
            ["indice_ms_mediana"] = "index_ms_median", ["inesperados"] = "unexpected", ["inferencia_agente"] = "agent_inference",
            ["inferior"] = "bottom_side", ["informe"] = "report", ["informe_referencia_bytes"] = "reference_report_bytes",
            ["iniciadas"] = "started_items", ["iniciar"] = "start", ["inicio"] = "start", ["inicio_lider"] = "leader_start",
            ["instalada"] = "installed", ["instalador"] = "installer", ["instalador_sha256"] = "installer_sha256", ["instalando"] = "installing",
            ["instancia"] = "instance", ["instancia_muestra"] = "sample_instance", ["instancias"] = "instances",
            ["instantanea_miembros"] = "members_snapshot", ["instantaneas_consulta"] = "query_snapshots",
            ["instruccion_usuario"] = "user_instruction", ["insuficiente"] = "insufficient", ["intencion"] = "intent",
            ["intencion_ms"] = "intent_ms", ["intencion_x"] = "intent_x", ["intenciones_pendientes"] = "pending_intents", ["intento"] = "attempt",
            ["intento_agente"] = "attempt_agent", ["intento_doc"] = "attempt_doc", ["intento_id"] = "attempt_id",
            ["intento_sesion"] = "attempt_session", ["intentos"] = "attempts", ["interfaz"] = "ui", ["interferencia"] = "interference",
            ["interno"] = "internal", ["interno_revit"] = "revit_internal", ["interrumpida"] = "interrupted", ["interrumpido"] = "interrupted",
            ["interseccion"] = "intersection", ["intersecciones"] = "intersections", ["invalida"] = "invalid", ["invalido"] = "invalid",
            ["inventada"] = "invented", ["inventado"] = "invented", ["inventar"] = "invent", ["inventario"] = "inventory",
            ["inventario_inicial"] = "initial_inventory", ["inyeccion_csharp_bloqueada"] = "csharp_injection_blocked", ["item_fuente"] = "source",
            ["items_fuente"] = "source_items", ["izq"] = "left_side", ["izquierda"] = "left", ["json_invalido"] = "invalid_json",
            ["justificacion"] = "justification", ["laboratorio"] = "lab", ["lado"] = "side", ["lados"] = "sides", ["lamina"] = "sheet",
            ["lamina_in"] = "sheet_in", ["lamina_pt"] = "sheet_pt", ["laminas"] = "sheets", ["laminas_vacias"] = "empty_sheets",
            ["lease_no_fiable"] = "lease_unreliable", ["lease_perdido"] = "lease_lost", ["lease_reiniciado"] = "lease_restarted",
            ["lease_renovar"] = "lease_renew", ["lease_validar"] = "lease_validate", ["lectura"] = "read", ["leer"] = "read",
            ["legible"] = "readable", ["lenguaje"] = "language", ["lenguaje_ambiguo"] = "ambiguous_language", ["leyenda"] = "legend",
            ["leyenda_bbox"] = "legend_bbox", ["liberada"] = "released", ["liberado"] = "released", ["liberar"] = "relinquish",
            ["libero_elementos"] = "relinquished_elements", ["libre"] = "free", ["libres"] = "free_items", ["lider"] = "leader",
            ["limitaciones"] = "limitations", ["limite"] = "limit", ["limpio"] = "clean", ["linea"] = "line", ["linea_base"] = "baseline",
            ["linea_corte"] = "cut_line", ["linea_proyeccion"] = "projection_line", ["lineas"] = "lines", ["lineas_descartadas"] = "discarded_lines",
            ["lista"] = "list", ["listo"] = "ready", ["listo_con_ambiguedades"] = "ready_with_ambiguities", ["llamada"] = "callout",
            ["llamada_luminaria"] = "fixture_callout", ["llamadas"] = "calls", ["llamadas_agente"] = "agent_calls",
            ["llamadas_audit"] = "callouts_audit", ["llamadas_cable"] = "wire_calls", ["llegada"] = "arrival",
            ["llegada_geometrica"] = "geometric_arrival", ["lleno"] = "full", ["longitud"] = "length", ["losa"] = "slab", ["losas"] = "slabs",
            ["lote"] = "batch", ["lote_chars"] = "batch_chars", ["lote_llamadas"] = "batch_calls", ["lote_max"] = "batch_max",
            ["lote_max_real"] = "actual_batch_max", ["lote_no_confirmado_sellado"] = "sealed_uncommitted_batch",
            ["lote_verificado"] = "verified_batch", ["lotes"] = "batches", ["lotes_confirmados"] = "committed_batches",
            ["lotes_totales"] = "total_batches_count", ["luces_en_vista"] = "lights_in_view", ["luminaria"] = "fixture",
            ["luminaria_id"] = "fixture_id", ["luminarias"] = "fixtures", ["luz"] = "light", ["láminas"] = "sheets", ["mal_colocados"] = "misplaced",
            ["malicioso"] = "malicious", ["mallas"] = "meshes", ["manifiesto"] = "manifest", ["manifiesto_paquete"] = "package_manifest",
            ["mano"] = "hand", ["mantener"] = "keep", ["manual_humano"] = "human_manual", ["mapa_texto"] = "text_map", ["mapa_tipos"] = "type_map",
            ["mapeo"] = "mapping", ["marca_cambios_sin_guardar_perdida"] = "unsaved_changes_flag_lost", ["marco"] = "frame", ["marron"] = "brown",
            ["marrón"] = "brown", ["mas"] = "more", ["matar"] = "kill", ["matriz"] = "matrix", ["max_alto_fila_pt"] = "max_row_height_pt",
            ["max_elementos"] = "max_elements", ["max_en_cola"] = "max_queued", ["max_en_curso"] = "max_running",
            ["max_por_agente"] = "max_per_agent", ["max_simbolo_pt"] = "max_symbol_pt", ["media"] = "medium", ["media_ms"] = "mean_ms_value",
            ["medicion_ms"] = "measurement_ms", ["medida"] = "measure", ["medio"] = "medium", ["medio_tono"] = "halftone",
            ["memoria_pico_kb"] = "peak_memory_kb", ["memoria_privada_delta_no_coleccionable_bytes"] = "private_memory_delta_noncollectible_bytes",
            ["memoria_proceso_mb"] = "process_memory_mb", ["mensaje"] = "message", ["metadatos"] = "metadata", ["metodo"] = "method",
            ["metodo_ejes"] = "grids_method", ["metricas"] = "metrics", ["miembros"] = "members", ["miembros_espejados"] = "mirrored_members",
            ["min_largo_frac"] = "min_length_frac", ["misma_o_anterior"] = "same_or_older", ["mismo_documento"] = "same_document",
            ["mismo_nivel"] = "same_level", ["mismo_tipo"] = "same_type", ["mismo_version_otro_commit"] = "same_version_other_commit",
            ["modelador"] = "modeler", ["modelo"] = "model", ["modelo_ft"] = "model_ft", ["modelo_modificado"] = "model_modified",
            ["modelo_sincronizado"] = "model_synced", ["modificado"] = "modified", ["modificado_antes"] = "modified_before",
            ["modificado_despues"] = "modified_after", ["modificado_sigue_true"] = "modified_still_true",
            ["modificado_sin_cambios"] = "modified_without_changes", ["modificado_tras_commit"] = "modified_after_commit",
            ["modificar_familia"] = "modify_family", ["modificar_proyecto"] = "modify_project", ["modo"] = "mode", ["modo_vinculo"] = "link_mode",
            ["modos"] = "modes", ["momento"] = "moment", ["montaje"] = "mounting", ["montaje_soportes"] = "mount_supports", ["montante"] = "riser",
            ["motivo"] = "reason", ["motivo_poblacion"] = "population_reason", ["motivos"] = "reasons", ["motor"] = "engine",
            ["motor_geometria"] = "geometry_engine", ["motores"] = "engines", ["mover"] = "move", ["movimiento"] = "motion",
            ["ms_acumulados"] = "accumulated_ms", ["ms_cajas"] = "boxes_ms", ["ms_mallas"] = "meshes_ms", ["ms_medio"] = "mean_ms",
            ["ms_ocupado"] = "busy_ms", ["ms_revit"] = "revit_ms_total", ["muerto"] = "dead", ["muestra"] = "sample",
            ["muestra_faltan"] = "missing_sample", ["muestra_sin_incidencia"] = "sample_without_incident", ["muestra_sobran"] = "extra_sample",
            ["muro"] = "wall", ["muros"] = "walls", ["métricas"] = "metrics", ["n_hojas"] = "n_sheets", ["nada"] = "nothing", ["naranja"] = "orange",
            ["nativo"] = "native", ["necesita_usuario"] = "needs_user", ["negrita"] = "bold", ["negro"] = "black", ["ninguna"] = "none",
            ["ninguno"] = "none", ["nivel"] = "level", ["nivel_id"] = "level_id", ["nivel_prueba_correspondencia"] = "correspondence_test_level",
            ["nivel_real"] = "actual_level", ["nivel_superior"] = "top_level", ["niveles"] = "levels", ["niveles_mcp"] = "mcp_levels",
            ["no_aplica_a_este_documento"] = "not_applicable_to_this_document", ["no_aplicada"] = "not_applied", ["no_aplicado"] = "not_applied",
            ["no_coleccionable_bytes_retenidos"] = "noncollectible_bytes_retained",
            ["no_coleccionable_ensamblados_nuevos"] = "noncollectible_new_assemblies", ["no_comprobable"] = "not_checkable",
            ["no_comprobado"] = "not_checked", ["no_confirmado"] = "not_committed", ["no_contiene"] = "not_contains",
            ["no_demostrada"] = "not_demonstrated", ["no_ejecutado"] = "not_run", ["no_ejecutado_ocupado"] = "not_run_busy",
            ["no_encontrado"] = "not_found", ["no_es_paquete"] = "not_a_package", ["no_escritos"] = "not_written",
            ["no_evaluable"] = "not_evaluable", ["no_existen"] = "missing", ["no_idempotente"] = "not_idempotent",
            ["no_independiente"] = "not_independent", ["no_instrumentadas"] = "not_instrumented", ["no_interpretable"] = "not_interpretable",
            ["no_necesario"] = "not_needed", ["no_persistio_como_aplicado"] = "not_persisted_as_applied", ["no_recuperable"] = "not_recoverable",
            ["no_restaurados"] = "not_restored", ["no_reutilizados"] = "not_reused", ["no_soportado"] = "not_supported",
            ["no_verificable"] = "not_verifiable", ["no_visible"] = "not_visible", ["nodos"] = "nodes", ["nombre"] = "name",
            ["nombre_en"] = "name_en", ["nombres_de_vista_repetidos"] = "repeated_view_names", ["nota"] = "note", ["notas"] = "notes",
            ["nube"] = "cloud", ["nueva"] = "new", ["nuevo"] = "new", ["nuevos"] = "new_items", ["numero"] = "number", ["número"] = "number",
            ["objetivo"] = "target", ["objetivos"] = "targets", ["observadas"] = "observed", ["observado"] = "observed",
            ["observado_ft"] = "observed_ft", ["observados"] = "observed_items", ["obstaculos"] = "obstacles", ["ocultar"] = "hide",
            ["ocultar_etiquetas_sin_ref"] = "hide_unreferenced_tags", ["oculto_en_vista"] = "hidden_in_view", ["ocupado"] = "busy",
            ["olvidar"] = "forget", ["omitida"] = "skipped_unit", ["omitido"] = "skipped", ["omitidos"] = "skipped", ["op_escritura"] = "write_op",
            ["op_vista"] = "op_view", ["opcion"] = "option", ["opcion_de_diseno"] = "design_option", ["opciones"] = "options",
            ["operacion"] = "operation", ["operacion_desconocida"] = "unknown_operation", ["operacion_ejecutada"] = "operation_executed",
            ["operaciones"] = "operations", ["operador"] = "operator", ["ops_sin_creacion_esperada"] = "ops_without_expected_creation",
            ["orden"] = "order", ["orden_desconocida"] = "unknown_command", ["orientacion"] = "orientation", ["origen"] = "origin",
            ["origen_clave"] = "origin_key", ["origen_huella"] = "origin_fingerprint", ["origen_mm"] = "origin_mm",
            ["origen_no_evaluable"] = "origin_not_evaluable", ["origen_por_tipo"] = "origin_by_type", ["original_firma"] = "original_signature",
            ["otra"] = "other", ["otro"] = "other", ["otro_flujo"] = "other_workflow", ["otro_id"] = "other_id", ["pagina"] = "page",
            ["pagina_pt"] = "page_pt", ["paginas"] = "pages", ["paginas_metodo"] = "pages_method", ["palabras"] = "words", ["papel"] = "paper",
            ["papel_in_por_pie"] = "paper_in_per_foot", ["papel_pt"] = "paper_pt", ["paquete_alterado"] = "package_tampered",
            ["para_colocar"] = "to_place", ["parametro"] = "parameter", ["parametro_clave"] = "key_parameter", ["parametro_id"] = "parameter_id",
            ["parametro_por_lote"] = "batch_parameter", ["parametro_sentido"] = "direction_parameter", ["parametros"] = "parameters",
            ["parametros_cols"] = "parameters_cols", ["parametros_requeridos"] = "required_parameters", ["parando"] = "stopping",
            ["parcial"] = "partial", ["pareja"] = "pair", ["pares"] = "pairs", ["pares_exactos"] = "exact_pairs", ["parrafo"] = "paragraph",
            ["parrafos"] = "paragraphs", ["partes_cuerpo"] = "body_parts", ["pasos"] = "steps", ["pasos_manuales"] = "manual_steps",
            ["pausa"] = "pause", ["pausado"] = "paused", ["pausar"] = "pause", ["pdf_aristas"] = "pdf_edges", ["pdf_carpeta"] = "pdf_folder",
            ["pdf_hojas"] = "pdf_sheets", ["pdf_nombre"] = "pdf_name", ["pendiente"] = "pending", ["pendiente_de_persona"] = "pending_person",
            ["pendientes"] = "pending_items", ["penetra"] = "penetrates", ["perfil"] = "profile", ["permiso"] = "permission",
            ["permisos"] = "permissions", ["permite"] = "allows", ["permitido"] = "allowed", ["permitir_anterior"] = "allow_downgrade",
            ["permitir_vacio"] = "allow_empty", ["persistente"] = "persistent", ["persona"] = "person", ["peticion_invalida"] = "invalid_request",
            ["pies_por_pt"] = "feet_per_pt", ["piso"] = "floor", ["pisos"] = "floors", ["pista"] = "hint", ["plafones"] = "ceiling_panels",
            ["plan_alterado"] = "plan_tampered", ["plan_ausente"] = "plan_missing", ["plan_autorizado"] = "plan_authorized",
            ["plan_completo_bytes"] = "full_plan_bytes", ["plan_desconocido"] = "unknown_plan", ["plan_distinto"] = "plan_different",
            ["plan_ejecucion"] = "execution_plan", ["plan_en_el_mismo_viaje"] = "plan_in_same_round_trip", ["plan_en_uso"] = "plan_in_use",
            ["plan_estado"] = "plan_status", ["plan_generado"] = "plan_generated", ["plan_guardado_invalido"] = "saved_plan_invalid",
            ["plan_ilegible"] = "plan_unreadable", ["plan_invalido"] = "plan_invalid", ["plan_rechazado"] = "plan_rejected",
            ["plan_recibido"] = "plan_received", ["plan_validado"] = "plan_validated", ["plan_valido"] = "plan_valid", ["planes"] = "plans",
            ["plano"] = "plan", ["planta"] = "floorplan", ["plantas"] = "floor_plans", ["plantilla"] = "template", ["plantilla_id"] = "template_id",
            ["plantillas"] = "templates", ["poblacion"] = "population", ["poblacion_no_evaluable"] = "population_not_evaluable",
            ["pocas_anclas"] = "few_anchors", ["politica"] = "policy", ["por_agente"] = "per_agent", ["por_clase"] = "by_class",
            ["por_codigo"] = "by_code", ["por_defecto"] = "default", ["por_dominio"] = "by_domain", ["por_error"] = "by_error",
            ["por_estado"] = "by_status", ["por_nivel"] = "by_level", ["por_op"] = "per_op", ["por_operacion"] = "per_operation",
            ["por_pagina"] = "per_page", ["por_parada"] = "by_stop", ["por_tipo"] = "by_type", ["por_workset"] = "by_workset",
            ["posicion"] = "position", ["posicion_en_cola"] = "queue_position", ["posición"] = "position",
            ["postcommand_aceptado"] = "postcommand_accepted", ["postcondicion"] = "postcondition", ["postcondiciones"] = "postconditions",
            ["practica"] = "practice", ["precedencia"] = "precedence", ["precondicion"] = "precondition", ["precondiciones"] = "preconditions",
            ["predeterminada"] = "default_value", ["preparada"] = "prepared", ["preparadas"] = "staged", ["preparar"] = "prepare",
            ["preparar_ms"] = "prepare_ms", ["presentacion"] = "presentation", ["preset_pdf"] = "pdf_preset", ["presupuesto_bytes"] = "budget_bytes",
            ["presupuesto_ms"] = "budget_ms", ["primario"] = "primary", ["primera"] = "first", ["principal"] = "main", ["prioridad"] = "priority",
            ["problemas"] = "problems", ["procedencia"] = "provenance", ["procedencia_rechazada"] = "provenance_rejected",
            ["procesamiento_resultado_ms"] = "result_processing_ms", ["proceso"] = "process", ["produccion"] = "production",
            ["producto"] = "product", ["prof_in"] = "depth_in", ["prof_mm"] = "depth_mm", ["profundidad"] = "depth", ["progreso"] = "progress",
            ["propietario"] = "owner_name", ["protecciones_0_6_1"] = "protections_0_6_1", ["protecciones_apagadas"] = "protections_off",
            ["protocolo"] = "protocol_info", ["prototipo"] = "prototype", ["proximidad"] = "proximity", ["proyecto"] = "project",
            ["prueba"] = "test", ["prueba_"] = "test_", ["pruebas"] = "tests_list", ["publicar"] = "publish",
            ["puede_tener_efectos"] = "may_have_effects", ["puente"] = "bridge", ["puente_ipc_total_ms"] = "bridge_ipc_total_ms",
            ["puerto"] = "port", ["puertos_fisicos"] = "physical_ports", ["punto"] = "point",
            ["punto_origen"] = "origin_point", ["punto_pt"] = "point_pt", ["puntos"] = "points",
            ["python_arranque_y_primer_trabajo_ms"] = "python_startup_and_first_job_ms", ["qc_desviaciones"] = "qc_deviations",
            ["qc_desviado"] = "qc_deviated", ["qc_ejecutado_con_evidencia"] = "qc_run_with_evidence", ["qc_final"] = "final_qc",
            ["qc_luminarias_tags"] = "qc_fixture_tags", ["qc_manifiesto"] = "qc_manifest", ["qnoexiste"] = "qdoesnotexist", ["que"] = "kind",
            ["quien"] = "who", ["radio"] = "radius", ["radio_mm"] = "radius_mm", ["radio_rotulo_pt"] = "label_radius_pt",
            ["ramas_faltantes"] = "missing_branches", ["rampa"] = "ramp", ["rampas"] = "ramps", ["rango"] = "range",
            ["rango_de_vista"] = "view_range_of", ["rango_vista"] = "view_range", ["rayos"] = "rays", ["rayos_dudosos"] = "doubtful_rays",
            ["rcp_todos"] = "rcp_all", ["reanudacion"] = "resumption", ["reanudar"] = "resume", ["reanudar_no_modifica"] = "resume_does_not_modify",
            ["reanudar_sin_escritura"] = "resume_without_write", ["reaperturas"] = "reopenings", ["receta"] = "recipe",
            ["receta_externa"] = "external_recipe", ["recetas"] = "recipes", ["rechazada"] = "rejected", ["rechazadas"] = "rejected_items",
            ["rechazadas_por_cola_llena"] = "rejected_queue_full", ["rechazadas_por_limite"] = "rejected_by_limit", ["rechazado"] = "rejected",
            ["rechazado_"] = "rejected_", ["rechazar"] = "reject", ["rechazo_backend"] = "backend_rejection",
            ["rechazo_fuera_de_alcance"] = "out_of_scope_rejection", ["rechazo_sin_autorizacion"] = "rejection_without_authorization",
            ["recibo"] = "receipt", ["recibo_distinto"] = "different_receipt", ["recibo_escritura"] = "write_receipt", ["recibos"] = "receipts",
            ["reclamos_de_lider_caido"] = "dead_leader_claims", ["recomendado"] = "recommended", ["reconciliacion"] = "reconciliation",
            ["reconciliada"] = "reconciled", ["reconciliado"] = "reconciled", ["reconciliados"] = "reconciled_items", ["reconciliar"] = "reconcile",
            ["reconciliar_antes"] = "reconcile_first", ["reconciliar_intencion"] = "reconcile_intent",
            ["reconciliar_no_aplicada"] = "reconcile_not_applied", ["reconstruir"] = "rebuild", ["recorte"] = "crop", ["rectangulo"] = "rectangle",
            ["rectangulos"] = "rectangles", ["recuperacion"] = "recovery", ["recuperacion_cabeza"] = "head_recovery",
            ["recuperacion_cola"] = "queue_recovery", ["recuperacion_rechazada"] = "recovery_rejected",
            ["recuperacion_reclamos"] = "claims_recovery", ["recuperación"] = "recovery", ["recuperada"] = "recovered",
            ["recuperado_por_otro_cliente_chars"] = "recovered_by_other_client_chars", ["red"] = "network",
            ["reduccion_chars_pct"] = "chars_reduction_pct", ["reduccion_llamadas_agente"] = "agent_calls_reduction",
            ["reduccion_pct"] = "reduction_pct", ["reduccion_tokens_estimados_agente"] = "agent_estimated_tokens_reduction",
            ["reducido"] = "reduced", ["reemplaza"] = "replaces", ["reemplazados_en_uso"] = "replaced_in_use", ["reemplazar"] = "replace",
            ["reemplazar_ids"] = "replace_ids", ["referencia"] = "reference", ["referencia_externa"] = "external_reference",
            ["referencias"] = "references", ["referencias_disponibles"] = "available_references", ["reflejado"] = "reflected",
            ["reflexion"] = "mirror", ["refrescar"] = "refresh", ["refs_destino"] = "target_refs", ["refs_estables"] = "stable_refs",
            ["regiones_de_plano"] = "plan_regions", ["registrada_en"] = "recorded_in", ["registro"] = "record", ["registros"] = "records",
            ["regla"] = "rule", ["regla_tags"] = "tags_rule", ["reglas"] = "rules", ["reinicio"] = "restart",
            ["reinicio_pendiente"] = "restart_pending", ["reintentable"] = "retryable", ["reintento"] = "retry", ["reintento_ms"] = "retry_ms",
            ["reintento_significa"] = "retry_means", ["reintentos"] = "retries", ["relectura_error"] = "reread_error",
            ["relectura_independiente"] = "independent_reread", ["relectura_valida"] = "reread_valid", ["releidos"] = "reread",
            ["releidos_tras_revertir"] = "reread_after_revert", ["relevancia"] = "relevance", ["relleno"] = "fill", ["relleno_corte"] = "cut_fill",
            ["relleno_superficie"] = "surface_fill", ["reparacion_antes_truncar"] = "repair_before_truncate",
            ["reparacion_tras_fragmento_tmp"] = "repair_after_tmp_fragment", ["reparacion_tras_truncar"] = "repair_after_truncate",
            ["repeticion"] = "repetition", ["repetir"] = "repeat", ["repositorio_actualizacion"] = "update_repo",
            ["reprogramacion_no_encolada"] = "reschedule_not_queued", ["requeridos"] = "required", ["requiere"] = "requires",
            ["requiere_ejecutor_ui"] = "requires_ui_executor", ["requiere_reconciliar"] = "requires_reconcile",
            ["requiere_verificacion"] = "requires_verification", ["requisito"] = "requirement", ["requisito_duplicado"] = "duplicate_requirement",
            ["requisito_sin_pagina"] = "requirement_without_page", ["requisito_vacio"] = "empty_requirement", ["requisitos"] = "requirements",
            ["resaltado"] = "highlighted", ["resaltar"] = "highlight", ["residuo_excesivo"] = "excessive_residual", ["residuo_ft"] = "residual_ft",
            ["resolver"] = "resolve", ["respaldos"] = "backups", ["respuesta_perdida_registrada_como_incierta"] = "lost_reply_recorded_as_uncertain",
            ["restauracion_addin"] = "addin_restoration", ["restaurados"] = "restored", ["restaurados_relectura"] = "restored_reread",
            ["restaurar"] = "restore", ["restriccion"] = "constraint", ["restricciones"] = "restrictions", ["resultado"] = "result",
            ["resultado_anterior"] = "previous_result", ["resultado_esperado"] = "expected_result",
            ["resultado_fuera_de_trama"] = "out_of_frame_result", ["resultado_original"] = "original_result",
            ["resultado_recortado"] = "truncated_result", ["resultados"] = "results", ["resumen"] = "summary", ["resumen_bytes"] = "summary_bytes",
            ["resumen_compacto"] = "compact_summary", ["resumen_compacto_chars"] = "compact_summary_chars",
            ["resumen_json_chars"] = "summary_json_chars", ["retardo_ms"] = "delay_ms", ["retener"] = "hold", ["retenido"] = "held",
            ["reutilizacion_por_verificar"] = "reuse_to_verify", ["reutilizadas"] = "reused_items", ["reutilizado"] = "reused",
            ["reutilizar"] = "reuse", ["rev_antes_despues"] = "rev_before_after", ["rev_instantanea"] = "snapshot_rev",
            ["rev_no_retrocede"] = "rev_does_not_go_back", ["revertida"] = "reverted", ["revertido"] = "reverted",
            ["revertido_verificado"] = "reverted_verified", ["revisados"] = "reviewed", ["revisar"] = "review",
            ["revisar_diferencias"] = "review_differences", ["revision_exigida"] = "required_revision", ["revision_incorrecta"] = "wrong_revision",
            ["revision_vinculo"] = "link_revision", ["revisor"] = "reviewer", ["revit_abiertos"] = "open_revits", ["revit_abrir"] = "revit_open",
            ["revit_archivo"] = "revit_file", ["revit_cerrando"] = "revit_closing", ["revit_cerrar"] = "revit_close",
            ["revit_destino"] = "target_revit", ["revit_elegidos"] = "revit_selected", ["revit_exportar"] = "revit_export",
            ["revit_guardar"] = "revit_save", ["revit_modificar"] = "revit_modify", ["revit_no_conectado"] = "revit_not_connected",
            ["revit_no_disponible"] = "revit_unavailable", ["revit_ocupado"] = "revit_busy", ["revit_transaccion"] = "revit_transaction",
            ["revocada"] = "revoked", ["revocadas"] = "revoked_items", ["rgb_vectorial"] = "vector_rgb",
            ["rgb_vectorial_no_encontrado"] = "vector_rgb_not_found", ["riser_geometria"] = "riser_geometry", ["robada"] = "stolen",
            ["robo"] = "theft", ["rojo"] = "red", ["rollback_confirmado"] = "rollback_confirmed", ["rollback_fallido"] = "rollback_failed",
            ["ronda"] = "round", ["rondas"] = "rounds", ["rosa"] = "pink", ["rotacion"] = "rotation", ["rotacion_modelo_deg"] = "model_rotation_deg",
            ["rotacion_pagina_deg"] = "page_rotation_deg", ["roto"] = "broken", ["rotulo"] = "label", ["rotulo_bbox"] = "label_bbox",
            ["rotulos_atravesados"] = "crossed_labels", ["rotulos_en_posicion"] = "labels_in_position",
            ["rotulos_sin_globo"] = "labels_without_bubble", ["rotulos_total"] = "labels_total", ["rust_ffi_desde"] = "rust_ffi_from",
            ["ruta"] = "path", ["ruta_audit"] = "route_audit", ["ruta_diario"] = "journal_path", ["ruta_distinta"] = "different_path",
            ["ruta_peligrosa"] = "dangerous_path", ["salida"] = "out", ["salida_proceso"] = "process_output", ["salir"] = "exit",
            ["salud"] = "health", ["sanitario"] = "sanitary", ["scripts_unicos"] = "unique_scripts", ["seccion"] = "section",
            ["sección"] = "section", ["secreto"] = "secret", ["seg"] = "sec", ["segmentos"] = "segments_list", ["seguidor"] = "follower",
            ["segunda"] = "second", ["segundos"] = "seconds", ["seleccionados"] = "selected", ["seleccionar"] = "select", ["sello"] = "seal",
            ["semejanza"] = "similarity", ["sentido"] = "direction", ["separada"] = "separated", ["separado"] = "separated",
            ["seq_confirmacion"] = "commit_seq", ["serializar_ms"] = "serialize_ms", ["servicio"] = "service", ["sesion"] = "session",
            ["sesion_intento"] = "attempt_session", ["sesiones"] = "sessions", ["sesiones_"] = "sessions_",
            ["set_laminas_persistido"] = "sheet_set_persisted", ["sha256_anterior"] = "previous_sha256", ["sha256_antes"] = "sha256_before",
            ["sha256_apartado"] = "set_aside_sha256", ["sha256_cambio"] = "sha256_changed", ["sha256_despues"] = "sha256_after",
            ["sha256_detalle"] = "detail_sha256", ["sha256_entrada"] = "input_sha256", ["sha256_esperado"] = "expected_sha256", ["si"] = "yes",
            ["si_no"] = "yes_no", ["siguiente"] = "next", ["siguiente_bytes"] = "next_bytes", ["siguiente_paso_seguro"] = "safe_next_step",
            ["simbolo"] = "symbol", ["simbolo_destino_ok"] = "target_symbol_ok", ["simbolo_origen_ok"] = "source_symbol_ok",
            ["simbolos"] = "symbols", ["simbolos_sha256"] = "symbols_sha256", ["simetria"] = "symmetry", ["simulado"] = "simulated",
            ["simular"] = "simulate", ["sin_altura"] = "no_height", ["sin_autorizacion"] = "without_authorization", ["sin_cambios"] = "unchanged",
            ["sin_cambios_pendientes_tras_guardar"] = "no_pending_changes_after_save", ["sin_commit"] = "no_commit",
            ["sin_commit_duplicado"] = "no_duplicate_commit", ["sin_conexion_alpha1"] = "no_connection_alpha1", ["sin_cuarto"] = "no_room",
            ["sin_cuerpo"] = "no_body", ["sin_datos_de_ejecucion"] = "no_execution_data",
            ["sin_discrepancias_entre_capas"] = "no_layer_discrepancies", ["sin_ejecutar"] = "not_run", ["sin_ejecutor_ui"] = "no_ui_executor",
            ["sin_etiqueta"] = "no_tag", ["sin_evento"] = "no_event", ["sin_globo"] = "no_bubble", ["sin_guardar_ni_sync"] = "not_saved_or_synced",
            ["sin_leyenda"] = "no_legend", ["sin_llegada"] = "no_arrival", ["sin_malla"] = "no_mesh", ["sin_medir"] = "unmeasured",
            ["sin_muro"] = "no_wall", ["sin_nivel"] = "no_level", ["sin_nombre"] = "unnamed", ["sin_nombre_modo_heredado"] = "unnamed_legacy_mode",
            ["sin_objetivos"] = "no_targets", ["sin_parametro"] = "no_parameter", ["sin_pendiente"] = "nothing_pending",
            ["sin_posicion_evaluable"] = "no_evaluable_position", ["sin_referencia"] = "no_reference", ["sin_registro"] = "unregistered",
            ["sin_releases"] = "no_releases", ["sin_resolver"] = "unresolved", ["sin_respuesta"] = "no_response", ["sin_revisar"] = "unreviewed",
            ["sin_sobrescritura"] = "no_overwrite", ["sin_solido"] = "no_solid", ["sin_superficie"] = "no_surface",
            ["sin_supervision"] = "unsupervised", ["sin_tag"] = "untagged", ["sin_tag_en_vista"] = "untagged_in_view", ["sin_tipo"] = "no_type",
            ["sin_verificar"] = "unverified", ["sincronizar"] = "sync", ["sistema"] = "system", ["sobran"] = "extra_items",
            ["sobrante"] = "leftover", ["sobrantes"] = "leftovers", ["sobrescribir"] = "overwrite", ["solicitadas"] = "requested",
            ["solicitudes"] = "requests", ["solidos"] = "solids", ["solo"] = "only", ["solo_ambiguedades"] = "only_ambiguities",
            ["solo_antes"] = "only_before", ["solo_despues"] = "only_after", ["solo_diagrama"] = "diagram_only", ["solo_lectura"] = "read_only",
            ["solo_lectura_tipo"] = "type_read_only", ["solo_pagina"] = "page_only", ["sonda"] = "probe", ["soporte"] = "support",
            ["soporte_cat"] = "support_cat", ["soporte_invalido"] = "invalid_support", ["soporte_uid"] = "support_uid",
            ["sospechoso"] = "suspicious", ["sospechosos"] = "suspects", ["stub_externo"] = "external_stub", ["stubs_externos"] = "external_stubs",
            ["sub_excluir"] = "sub_exclude", ["subdominio"] = "subdomain", ["subrayado"] = "underline", ["sueltas_chars"] = "single_chars",
            ["sueltas_llamadas"] = "single_calls", ["sugerencia"] = "suggestion", ["sugerencia_vistas"] = "views_suggestion",
            ["superficies"] = "surfaces", ["superior"] = "top", ["superposicion"] = "overlap", ["superposiciones"] = "overlaps",
            ["superpuest"] = "overlap_prefix", ["supervision_al_vinculo"] = "link_supervision", ["sync_antes"] = "sync_before", ["sí"] = "yes",
            ["sí_no"] = "yes_no", ["tabla"] = "schedule", ["tabla_audit"] = "schedule_audit", ["tabla_csv"] = "schedule_csv",
            ["tabla_id"] = "schedule_id", ["tablas"] = "tables", ["tablero"] = "panel", ["tags_de_luminarias"] = "fixture_tags",
            ["tags_duplicados"] = "duplicate_tags", ["tags_duplicados_sobrantes"] = "leftover_duplicate_tags", ["tags_huerfanos"] = "orphan_tags",
            ["tam"] = "size", ["tam_etiqueta"] = "tag_size", ["tamano_pt"] = "size_pt", ["tamano_tuberia"] = "pipe_size",
            ["tamaño_tuberia"] = "pipe_size", ["tarea"] = "task", ["tarea_barrera_"] = "task_barrier_", ["tarea_lote"] = "task_batch",
            ["tareas"] = "tasks", ["techo"] = "ceiling", ["techos"] = "ceilings", ["telemetria"] = "telemetry", ["tema"] = "topic",
            ["temporal"] = "temporary", ["terminal_fin"] = "end_terminal", ["terminal_inicio"] = "start_terminal", ["terminales"] = "terminals",
            ["terminales_audit"] = "terminals_audit", ["testigo"] = "witness", ["texto"] = "text", ["texto_ausente"] = "missing_text",
            ["texto_esperado"] = "expected_text", ["texto_no_evaluable"] = "text_not_evaluable", ["textos"] = "texts",
            ["textos_extraibles"] = "extractable_texts", ["tiempo_agotado"] = "timed_out", ["tiempos_commit"] = "commit_timings",
            ["tiempos_consulta"] = "query_timings", ["tiempos_plan"] = "plan_timings", ["tiene_imagen"] = "has_image", ["tipo"] = "type",
            ["tipo_actual"] = "current_type", ["tipo_ambiguo"] = "ambiguous_type", ["tipo_destino"] = "target_type",
            ["tipo_etiqueta"] = "tag_type_name", ["tipo_grupo"] = "group_type", ["tipo_id"] = "type_id", ["tipo_incorrecto"] = "wrong_type",
            ["tipo_observado"] = "observed_type", ["tipo_origen"] = "source_type", ["tipos"] = "types", ["tipos_catalogo"] = "catalog_types",
            ["tipos_de_aviso"] = "warning_types", ["tipos_de_familia"] = "family_types", ["titulo"] = "title", ["titulo_antes"] = "title_before",
            ["toca"] = "touches", ["toca_sin_conectar"] = "touches_unconnected", ["todo"] = "all", ["todos"] = "all",
            ["todos_tipos"] = "all_types", ["tokens_estimados"] = "estimated_tokens", ["tokens_estimados_agente"] = "agent_estimated_tokens",
            ["tokens_estimados_bajo_demanda"] = "on_demand_estimated_tokens", ["tokens_estimados_cable"] = "wire_estimated_tokens",
            ["tokens_estimados_catalogo"] = "catalog_estimated_tokens", ["tokens_metodo"] = "tokens_method", ["tolerancia"] = "tolerance",
            ["tolerancia_en_otra_unidad"] = "tolerance_in_other_unit", ["tolerancia_ft"] = "tolerance_ft", ["tolerancias"] = "tolerances",
            ["total_conectores"] = "connectors_total", ["total_lineas"] = "total_lines", ["trabajadores"] = "workers", ["trabajo"] = "job",
            ["trabajos"] = "jobs", ["trabajos_en_curso"] = "running_jobs", ["tramo"] = "segment", ["tramo_id"] = "segment_id",
            ["tramo_max_ms"] = "max_segment_ms", ["tramos"] = "segments", ["tramos_faltantes"] = "missing_segments", ["transaccion"] = "transaction",
            ["transacciones"] = "transactions_list", ["transcripcion"] = "transcription", ["transformacion"] = "transformation",
            ["transformacion_fallida"] = "transformation_failed", ["transicion"] = "transition", ["transicion_ilegal"] = "illegal_transition",
            ["transicion_inconsistente"] = "inconsistent_transition", ["transitorias_ko"] = "transient_ko", ["transitorias_ok"] = "transient_ok",
            ["transitorio"] = "transient", ["transparencia"] = "transparency", ["tras_cabeza"] = "after_head", ["tras_commit"] = "after_commit",
            ["tras_confirmar_parcial"] = "after_partial_commit", ["tras_escribir_antes_fsync"] = "after_write_before_fsync",
            ["tras_fsync_antes_cabeza"] = "after_fsync_before_head", ["tras_guardar"] = "after_save", ["tras_reanudar"] = "after_resume",
            ["tras_reclamo"] = "after_claim", ["tras_recuperar"] = "after_recover", ["trasguardar"] = "aftersave",
            ["traslacion_ft"] = "translation_ft", ["traza"] = "trace", ["trazabilidad"] = "traceability", ["trazado"] = "trace_path",
            ["trazo"] = "stroke", ["triangulos"] = "triangles", ["truncado"] = "truncated", ["tuberia"] = "pipe",
            ["tuberia_error"] = "pipe_error", ["tuberias"] = "pipes", ["tx_estado"] = "tx_status", ["tx_por_op"] = "tx_per_op",
            ["ubicacion"] = "location", ["uid_luminaria"] = "fixture_uid", ["ultima_actividad"] = "last_activity", ["ultimo"] = "last",
            ["ultimo_guardado"] = "last_save", ["un_estado_nuevo_desconocido"] = "an_unknown_new_status", ["un_solo_commit"] = "single_commit",
            ["unica"] = "unique", ["unico"] = "unique", ["unidad"] = "unit", ["unidad_usuario"] = "user_unit", ["unidades"] = "units",
            ["unidades_en_modelo"] = "units_in_model", ["unidades_no_explicitas"] = "implicit_units", ["unidades_total"] = "units_total",
            ["uno"] = "one", ["uno_a_uno"] = "one_to", ["usada"] = "used", ["usada_por"] = "used_by", ["uso"] = "usage", ["usuario"] = "user",
            ["vacio"] = "empty_value", ["vacio_no_permitido"] = "empty_not_allowed", ["vacio_permitido"] = "empty_allowed", ["vacios"] = "empties",
            ["validaciones"] = "validations", ["validada"] = "validated", ["validador"] = "validator", ["validar"] = "validate",
            ["valido_firmado"] = "valid_signed", ["valido_sin_firma"] = "valid_unsigned", ["valor"] = "value", ["valor_esperado"] = "expected_value",
            ["valores"] = "values", ["valores_originales"] = "original_values", ["valores_tras_commit"] = "values_after_commit",
            ["variantes"] = "variants", ["varias_instancias"] = "multiple_instances", ["veces"] = "times", ["vectores"] = "vectors",
            ["vence"] = "expires", ["vencido"] = "expired", ["ventilacion"] = "vent", ["verde"] = "green", ["veredicto"] = "verdict",
            ["verificacion"] = "verification", ["verificacion_addin"] = "addin_verification", ["verificacion_creacion"] = "creation_verification",
            ["verificacion_disco"] = "disk_verification", ["verificacion_fallida"] = "verification_failed",
            ["verificacion_nativa"] = "native_verification", ["verificacion_ok"] = "verification_ok",
            ["verificacion_temporal"] = "temporary_verification", ["verificacion_temporal_addin"] = "addin_temporary_verification",
            ["verificada"] = "verified", ["verificada_por_observacion"] = "verified_by_observation", ["verificadas"] = "verified",
            ["verificado"] = "verified", ["verificador"] = "verifier_name", ["verificar"] = "verify", ["verificar_disco"] = "verify_disk",
            ["verificar_independiente"] = "verify_independent", ["verificar_puertos"] = "verify_ports", ["verificar_qc"] = "verify_qc",
            ["version_cliente"] = "client_version_name", ["version_no_soportada"] = "unsupported_version", ["versiones"] = "versions",
            ["verticales"] = "verticals", ["vft_planta"] = "vft_plan", ["viejo"] = "old", ["viewport_tipo"] = "viewport_type", ["viga"] = "beam",
            ["vigas"] = "beams", ["vigente"] = "current_valid", ["vinculado_id"] = "linked_id", ["vinculo"] = "link",
            ["vinculo_cargado"] = "link_loaded", ["vinculo_id"] = "link_id", ["vinculo_reflejado"] = "mirrored_link",
            ["vinculo_seleccionado"] = "selected_link", ["vinculos"] = "links", ["vinculos_cargados"] = "loaded_links",
            ["vinculos_revit"] = "revit_links", ["violeta"] = "violet", ["visible_segun_api"] = "visible_per_api",
            ["visibles_en_vista"] = "visible_in_view", ["visita_max_ms"] = "max_visit_ms", ["visitas"] = "visits",
            ["visitas_antes_de_iniciar"] = "visits_before_start", ["visitas_largas"] = "long_visits", ["vista"] = "view",
            ["vista_activa"] = "active_view", ["vista_audit"] = "view_audit", ["vista_etiquetas"] = "tags_view", ["vista_id"] = "view_id",
            ["vista_planta"] = "plan_view", ["vista_rcp"] = "rcp_view", ["vista_ref"] = "view_ref_of", ["vista_tipo"] = "view_type",
            ["vista_vinculada_id"] = "linked_view_id", ["vistas"] = "views", ["vistas_cols"] = "views_cols", ["vistas_metodo"] = "views_method",
            ["vistas_omitidas"] = "skipped_views", ["vistas_sin_lamina"] = "views_without_sheet", ["vol_parcial"] = "partial_vol",
            ["volumen_ft3"] = "volume_ft3", ["volumenes_parciales"] = "partial_volumes", ["vínculos"] = "links",
            ["wal_antiguo_descartado"] = "old_wal_discarded", ["wal_linea_final_truncada"] = "wal_truncated_last_line",
            ["workflow_finalizado"] = "workflow_finished", ["workset_oculto"] = "hidden_workset", ["ya_aplicado"] = "already_applied",
            ["ya_cargada"] = "already_loaded", ["ya_en_curso"] = "already_running", ["ya_estaba"] = "already_there",
            ["ya_existe"] = "already_exists", ["ya_existen"] = "already_exist", ["ya_existia"] = "already_existed", ["z_verificar"] = "z_check",
            ["zombi"] = "zombie", ["zona"] = "zone", ["área"] = "area",
            ["huella"] = "fingerprint", ["temporal_ok"] = "temporary_ok", ["restauracion_ok"] = "restoration_ok", ["motor_ms"] = "engine_ms",
            // Task transaction policy (enum names stored in task records).
            ["PorUnidad"] = "PerUnit", ["Ninguna"] = "None", ["Lectura"] = "ReadOnly",
        };

        /// <summary>English token for a legacy one; any other text is returned unchanged.</summary>
        public static string Token(string s) => s != null && Tokens.TryGetValue(s, out var e) ? e : s;

        public static bool IsLegacy(string s) => s != null && Tokens.ContainsKey(s);

        /// <summary>The legacy "yes" ("si"/"sí", any case) of boolean flags and yes/no parameters. Callers decide whether the value is a flag.</summary>
        public static bool IsLegacyYes(string s) => s != null && (s.Equals("si", StringComparison.OrdinalIgnoreCase) || s.Equals("sí", StringComparison.OrdinalIgnoreCase));

        /// <summary>The legacy "none" ("ninguna"/"ninguno", any case) of template=, scope_box= and element-pointing parameter values.</summary>
        public static bool IsLegacyNone(string s) => s != null && (s.Equals("ninguna", StringComparison.OrdinalIgnoreCase) || s.Equals("ninguno", StringComparison.OrdinalIgnoreCase));

        /// <summary>Frame keys of Request/Response: English name → legacy name.</summary>
        public static readonly IReadOnlyDictionary<string, string> FrameKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attempt_id"] = "intento_id", ["status"] = "estado", ["data"] = "datos", ["warning"] = "aviso",
        };

        /// <summary>Value under <paramref name="key"/>, or under its legacy alias when the key is absent.</summary>
        public static bool TryGet(IDictionary<string, object> o, string key, out object value)
        {
            value = null;
            if (o == null) return false;
            if (o.TryGetValue(key, out value)) return true;
            return FrameKeys.TryGetValue(key, out var legacy) && o.TryGetValue(legacy, out value);
        }

        /// <summary>String value under <paramref name="key"/> or its legacy alias (null if neither is present).</summary>
        public static string GetString(IDictionary<string, object> o, string key) =>
            TryGet(o, key, out var v) && v != null ? v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

        /// <summary>
        /// Legacy category shorthands in geo cat=/against= values → the English shorthand. Differs from <see cref="Tokens"/> on purpose:
        /// in 0.9.3 "techos" meant Revit Roofs (Ceilings were "cielos"/"plafones").
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> CategoryAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["losas"] = "slabs", ["pisos"] = "floors", ["vigas"] = "beams", ["columnas"] = "columns", ["rampas"] = "ramps", ["escaleras"] = "stairs",
            ["techos"] = "roofs", ["cubiertas"] = "roofs", ["cielos"] = "ceilings", ["plafones"] = "ceiling_panels", ["muros"] = "walls",
            ["genericos"] = "generic", ["luminarias"] = "fixtures", ["estructura"] = "structure",
        };

        /// <summary>0.9.4 prefix of a person's statement in a delivery receipt (now "user:").</summary>
        public const string UserOriginPrefix = "usuario:";

        /// <summary>The bridge's old command-line switch for the full profile.</summary>
        public const string FullProfileSwitch = "--completo";

        // ---------------- MCP input ----------------

        /// <summary>Arguments whose string values are enum-like tokens (mapped through <see cref="Tokens"/>).</summary>
        private static readonly HashSet<string> ValueKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "action", "mode", "tx", "kind", "side", "links", "flow", "dimension", "all_types", "create", "data_type", "overwrite",
            "decision", "reconcile", "at_end", "on_error", "color_by", "detail", "mount", "option", "recipe", "save", "discard",
            "new", "combine", "order", "views", "scope", "profile", "runner", "orientation", "phase", "status",
        };

        /// <summary>Values whose meaning depends on the argument (the generic token means something else there).</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> ValueOverrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["detail"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["bajo"] = "coarse" },
        };

        /// <summary>Containers of user data (Revit parameter names, script arguments, payloads): their keys are never renamed.</summary>
        private static readonly HashSet<string> Opaque = new HashSet<string>(StringComparer.Ordinal)
        {
            "args", "params", "values", "data", "result", "expected", "where", "fields", "ids", "refs", "docs", "evidence",
        };

        /// <summary>Argument names that only mean something else for one tool or operation.</summary>
        private static Dictionary<string, string> KeyOverrides(string op, string opName)
        {
            if (op == Wire.OpExec) return new Dictionary<string, string>(StringComparer.Ordinal) { ["guardar"] = "save_as" };
            if (op == Wire.OpFam) return new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "into", ["flujo"] = "flow" };   // connector flow, not a workflow
            if (op == Wire.OpDoc) return new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "into" };
            if (opName == "view_create") return new Dictionary<string, string>(StringComparer.Ordinal) { ["origen"] = "source" };
            return null;
        }

        /// <summary>Argument names that only mean something else for one MCP tool (by tool name), or null.</summary>
        public static Dictionary<string, string> ToolArgOverrides(string tool) => KeyOverrides(tool, null);

        /// <summary>
        /// Normalizes the arguments of one request (MCP tool call or bridge → add-in request) in place: legacy argument names, enum-like
        /// values and the arguments of each operation in ops are turned into English. Idempotent; English input is left untouched.
        /// </summary>
        public static void NormalizeArgs(string op, IDictionary<string, object> a)
        {
            if (a == null) return;
            RenameKeys(a, KeyOverrides(op, null));
            MapValues(a, null);
            // Connector system= is an enum (cold_water, drain...); elsewhere system= names the user's system type and is never rewritten.
            if (op == Wire.OpFam && a.TryGetValue("system", out var sys) && sys is string ss) a["system"] = MapValue("system", ss);
            if (op == Wire.OpExec && a.TryGetValue("name", out var n) && n is string ns && string.Equals(ns.Trim(), "lista", StringComparison.OrdinalIgnoreCase)) a["name"] = "list";
            if (a.TryGetValue("ops", out var ops) && ops is List<object> list)
                foreach (var o in list) if (o is IDictionary<string, object> d) NormalizeOp(d);
        }

        /// <summary>Operation arguments (plan/commit ops, held plans): names, nested names (range…) and enum-like values.</summary>
        public static void NormalizeOp(IDictionary<string, object> d)
        {
            if (d == null) return;
            var opName = Arg.GetString(d, "op");
            RenameKeys(d, KeyOverrides(null, opName));
            MapValues(d, opName);
            foreach (var k in d.Keys.ToList())
                if (!Opaque.Contains(k) && d[k] is IDictionary<string, object> child) RenameKeys(child, null);
        }

        /// <summary>Enum-like inputs of the INSTALLED recipes (save=si, before=guardar, direction=arriba...).</summary>
        private static readonly string[] RecipeEnumArgs = { "save", "save_at_end", "before", "direction" };

        /// <summary>
        /// args= of an INSTALLED recipe (whose input names this project owns): 0.9.4 names (vista, parametro, valor, guardar, ...) and the
        /// few enum-like values are renamed. Values such as valor= are never rewritten. Never call it for user or external recipes.
        /// </summary>
        public static void NormalizeRecipeArgs(IDictionary<string, object> args)
        {
            if (args == null) return;
            RenameKeys(args, null);
            foreach (var k in RecipeEnumArgs)
                if (args.TryGetValue(k, out var v) && v is string s) args[k] = IsLegacyYes(s.Trim()) ? "yes" : MapValue(k, s);
        }

        /// <summary>Legacy tokens that are also English words: accepted as verbs, never renamed as keys.</summary>
        private static readonly HashSet<string> NotKeys = new HashSet<string>(StringComparer.Ordinal) { "red", "media", "solo", "once", "real", "total", "final" };

        private static void RenameKeys(IDictionary<string, object> d, Dictionary<string, string> overrides)
        {
            foreach (var k in d.Keys.ToList())
            {
                var target = overrides != null && overrides.TryGetValue(k, out var o) ? o : NotKeys.Contains(k) ? k : Token(k);
                if (target == k) continue;
                if (!d.ContainsKey(target)) d[target] = d[k];
                d.Remove(k);
            }
        }

        private static void MapValues(IDictionary<string, object> d, string opName)
        {
            foreach (var k in d.Keys.ToList())
            {
                var v = d[k];
                var enumLike = ValueKeys.Contains(k) || (k == "type" && opName == "view_create");
                // Only enum-like values are mapped: value=, text=, name=... are the user's and are never rewritten (value=Sí stays "Sí").
                if (v is string s && enumLike) d[k] = IsLegacyYes(s.Trim()) ? "yes" : MapValue(k, s);
                else if (v is string ts && k == "template" && IsLegacyNone(ts.Trim())) d[k] = "none";   // any other template= is a view template NAME
                else if (enumLike && v is List<object> l)
                    for (var i = 0; i < l.Count; i++) if (l[i] is string li) l[i] = MapValue(k, li);
            }
        }

        private static string MapValue(string key, string value)
        {
            if (value.IndexOf(',') >= 0) return string.Join(",", value.Split(',').Select(p => MapValue(key, p)));
            var t = value.Trim();
            var lower = t.ToLowerInvariant();
            if (ValueOverrides.TryGetValue(key, out var ov) && ov.TryGetValue(lower, out var o)) return o;
            return Tokens.TryGetValue(lower, out var e) ? e : value;
        }

        // ---------------- persisted records ----------------

        /// <summary>Keys whose values are statuses or decisions (persisted with legacy values by 0.9.3 and earlier).</summary>
        private static readonly HashSet<string> StatusKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "status", "plan_status", "job_status", "receipt_status", "op_status", "group_status", "tx_status", "decision", "policy", "phase", "mode", "retry", "ev",
        };

        /// <summary>
        /// A record read from disk (journal line, task, plan, receipt, manifest…), normalized in place: legacy keys renamed (recursively,
        /// except inside user-data containers) and legacy status values mapped. Call it AFTER any hash or signature check over the stored
        /// bytes; never re-serialize a hash-chained record from the result.
        /// </summary>
        public static Dictionary<string, object> NormalizeRecord(Dictionary<string, object> o)
        {
            if (o != null) NormalizeRecord(o, 0);
            return o;
        }

        /// <summary>Record keys that only exist in persisted data (receipt and delivery timestamps).</summary>
        private static readonly Dictionary<string, string> RecordKeyOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "in" };

        private static void NormalizeRecord(IDictionary<string, object> o, int depth)
        {
            if (depth > 32) return;
            RenameKeys(o, RecordKeyOverrides);
            foreach (var k in o.Keys.ToList())
            {
                var v = o[k];
                if (v is string s && StatusKeys.Contains(k)) o[k] = Token(s);
                else if (Opaque.Contains(k)) continue;
                else if (v is IDictionary<string, object> child) NormalizeRecord(child, depth + 1);
                else if (v is List<object> list)
                    foreach (var item in list) if (item is IDictionary<string, object> c) NormalizeRecord(c, depth + 1);
            }
        }

        // ---------------- config.json ----------------

        private static readonly Dictionary<string, string> ConfigKeyOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actualizacion_sin_firma"] = "unsigned_updates", ["carpetas"] = "folders",
        };

        /// <summary>
        /// config.json is written by IT or the user (and may be deployed by policy): it is never rewritten to migrate it. Legacy keys and
        /// values (profile, runner, permission names) are read as their English equivalents; when both spellings are present, English wins.
        /// </summary>
        public static Dictionary<string, object> NormalizeConfig(Dictionary<string, object> o)
        {
            if (o == null) return null;
            RenameKeys(o, ConfigKeyOverrides);
            foreach (var k in new[] { "profile", "runner" })
                if (o.TryGetValue(k, out var v) && v is string s && IsLegacy(s.Trim().ToLowerInvariant())) o[k] = Token(s.Trim().ToLowerInvariant());
            if (o.TryGetValue("permissions", out var p) && p is List<object> pl)
                for (var i = 0; i < pl.Count; i++) if (pl[i] is string ps) pl[i] = Token(ps.Trim());
            foreach (var k in new[] { "lab", "power_engine" })
                if (o.TryGetValue(k, out var c) && c is IDictionary<string, object> child) RenameKeys(child, ConfigKeyOverrides);
            return o;
        }

        /// <summary>A stored response payload replayed on a repeated key: only its status key and value are mapped; the data stays as stored.</summary>
        public static object NormalizePayloadStatus(object payload)
        {
            if (!(payload is IDictionary<string, object> d)) return payload;
            foreach (var k in new[] { "estado", "status" })
                if (d.TryGetValue(k, out var v) && v is string s) { d.Remove(k); d["status"] = Token(s); }
            if (d.TryGetValue("datos", out var data) && !d.ContainsKey("data")) { d.Remove("datos"); d["data"] = data; }
            if (d.TryGetValue("aviso", out var w) && !d.ContainsKey("warning")) { d.Remove("aviso"); d["warning"] = w; }
            return d;
        }

        // ---------------- operation fingerprints ----------------

        private static readonly Lazy<Dictionary<string, string>> Reverse = new Lazy<Dictionary<string, string>>(() =>
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            // Ambiguous English names: the legacy name that the 0.9.3 MCP vocabulary actually used.
            foreach (var kv in new Dictionary<string, string> { ["height"] = "altura", ["source"] = "origen", ["sheets"] = "hojas", ["sheet"] = "hoja",
                         ["plan"] = "plan", ["status"] = "estado", ["type"] = "tipo", ["view"] = "vista", ["level"] = "nivel", ["target"] = "destino",
                         ["scope"] = "alcance", ["key"] = "clave", ["task"] = "tarea", ["low"] = "bajo", ["none"] = "ninguna", ["in"] = "entrada",
                         ["save_as"] = "guardar_como", ["open"] = "abrir", ["warnings"] = "avisos", ["committed"] = "confirmados", ["created"] = "creados",
                         ["new"] = "nuevo", ["unknown"] = "desconocido", ["reverted"] = "revertido", ["id"] = "id", ["yes"] = "si" })
                r[kv.Key] = kv.Value;
            foreach (var kv in Tokens.OrderBy(x => x.Key, StringComparer.Ordinal))
                if (!r.ContainsKey(kv.Value)) r[kv.Value] = kv.Key;
            return r;
        });

        /// <summary>
        /// The arguments as a 0.9.3 client would have sent them (legacy names and values), to recognize a fingerprint stored by an older
        /// version for the same operation. Best effort: an English name with several legacy spellings uses the one the old MCP vocabulary used.
        /// </summary>
        public static object ToLegacy(object value) => ToLegacy(value, false, 0);

        private static object ToLegacy(object value, bool enumLike, int depth)
        {
            if (depth > 32) return value;
            switch (value)
            {
                case string s: return enumLike && Reverse.Value.TryGetValue(s, out var l) ? l : s;
                case IDictionary<string, object> d:
                    var o = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var kv in d)
                    {
                        var k = Reverse.Value.TryGetValue(kv.Key, out var lk) ? lk : kv.Key;
                        // template/system are no longer mapped on input but 0.9.4 clients sent them in Spanish (ninguna, agua_fria...).
                        o[k] = Opaque.Contains(kv.Key) ? kv.Value : ToLegacy(kv.Value, ValueKeys.Contains(kv.Key) || kv.Key == "type" || kv.Key == "template" || kv.Key == "system", depth + 1);
                    }
                    return o;
                case List<object> list: return list.Select(x => ToLegacy(x, enumLike, depth + 1)).ToList();
                default: return value;
            }
        }

        public static string LegacyOp(string op) => op == Wire.OpTask ? "tarea" : op;

        // ---------------- data folders ----------------

        /// <summary>English data folder / file name → the name used up to 0.9.3 (reader fallback; LegacyMigration moves them).</summary>
        public static readonly IReadOnlyDictionary<string, string> HomeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = "tareas", ["task.json"] = "tarea.json", ["recipe.cs"] = "receta.cs", ["units.jsonl"] = "unidades.jsonl", ["_archive"] = "_archivo",
            ["queue"] = "cola", ["quarantine"] = "cuarentena", ["recipes"] = "recetas", ["audit"] = "auditoria", ["audit.jsonl"] = "auditoria.jsonl",
            ["backups"] = "respaldos", ["backup.json"] = "respaldo.json", ["files"] = "archivos", ["updates"] = "actualizaciones", ["staged"] = "preparadas",
            ["captures"] = "capturas", ["results"] = "resultados", ["history"] = "historial", ["operations"] = "operaciones",
            ["operations.jsonl"] = "operaciones.jsonl", ["authorizations"] = "autorizaciones", ["key.bin"] = "clave.bin", ["requests"] = "solicitudes",
            ["metrics"] = "metricas", ["workflows"] = "flujos", ["families"] = "familias", ["index.json"] = "indice.json", ["plans"] = "planes",
            ["model"] = "modelo", ["model.json"] = "modelo.json", ["ui.json"] = "interfaz.json", ["journal.jsonl"] = "diario.jsonl",
            ["state.json"] = "estado.json", ["pending.json"] = "pendiente.json", ["result.json"] = "resultado.json", ["last.txt"] = "ultima.txt",
            ["trusted_references.txt"] = "referencias_confiables.txt", ["calibration.json"] = "calibracion.json", ["service.jsonl"] = "servicio.jsonl",
            ["installer.lock"] = "instalador.lock", ["hold.on"] = "retener.on", ["paused.on"] = "pausado.on", ["tasks_documents"] = "tareas_documentos",
        };

        /// <summary>
        /// Reader fallback for one version: <paramref name="path"/> is an English path under <paramref name="root"/>. Walking down from
        /// the root, wherever the English entry is missing but the legacy one exists, the legacy one is followed. Data written by 0.9.3 and
        /// earlier keeps working in place until the installer migrates it; once the English entry exists it always wins.
        /// </summary>
        /// <summary>Installer switch an add-in up to 0.9.3 passes to the (new) installer of a staged ZIP update.</summary>
        public const string ApplyPendingSwitch = "aplicar-pendiente";

        private static readonly Dictionary<string, string> SetupSwitches = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actualizar"] = "update", ["desinstalar"] = "uninstall", ["reemplazar"] = "replace", ["borrardatos"] = "removedata", ["restaurar"] = "restore",
            ["niveles"] = "levels", ["diagnostico"] = "diagnostics", ["diagnóstico"] = "diagnostics", ["permitir-anterior"] = "allow-older",
            [ApplyPendingSwitch] = "apply-pending",
        };

        /// <summary>
        /// Installer switch (lower case, without the leading / or -) as English: the 0.9.4 names used by IT scripts and by older add-ins
        /// launching a new installer are mapped, "name=value" keeps its value. Any other switch is returned unchanged.
        /// </summary>
        public static string SetupSwitch(string low)
        {
            if (low == null) return null;
            var eq = low.IndexOf('=');
            return SetupSwitches.TryGetValue(eq < 0 ? low : low.Substring(0, eq), out var en) ? en + (eq < 0 ? "" : low.Substring(eq)) : low;
        }

        /// <summary>Plan entry keys of view graphic overrides in held plans written by 0.9.3 and earlier.</summary>
        public static string EntryKey(string key) =>
            key != null && key.StartsWith("graficos@", StringComparison.Ordinal) ? "graphics@" + key.Substring("graficos@".Length) : Token(key);

        /// <summary>Schema of install backups made by 0.9.3 and earlier.</summary>
        public const string BackupSchema = "bimbridge.respaldo/1";

        /// <summary>Entry names and schema of ZIP update packages built by 0.9.3 and earlier.</summary>
        public const string UpdateManifestName = "bimbridge-actualizacion.json";
        public const string UpdateSignatureName = "bimbridge-actualizacion.firma";
        public const string UpdateSchema = "bimbridge.actualizacion/1";

        /// <summary>Suffix of the audit chain head file up to 0.9.3 (now ".head").</summary>
        public const string AuditHeadSuffix = ".cabeza";

        public static string ResolveExisting(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return path;
            var full = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
            var cur = r;
            foreach (var part in full.Substring(r.Length + 1).Split(Path.DirectorySeparatorChar))
            {
                var next = Path.Combine(cur, part);
                if (!Directory.Exists(next) && !File.Exists(next) && HomeNames.TryGetValue(part, out var legacy))
                {
                    var old = Path.Combine(cur, legacy);
                    if (Directory.Exists(old) || File.Exists(old)) next = old;
                }
                cur = next;
            }
            return cur;
        }
    }
}
