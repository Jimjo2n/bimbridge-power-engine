// SPDX-License-Identifier: GPL-3.0-only
//! Adaptador JSONL pe=1. La geometría se ejecuta en los mismos kernels que FFI.
use bimbridge_power_engine::{kernels::*, PROTOCOL, VERSION};
use serde_json::{json, Value};
use std::io::{self, BufRead, Read, Write};
use std::time::Instant;

const JOBS: [&str; 6] = ["transform3", "nearest", "match", "duplicates", "segment_contacts", "components"];
const MAX_LINE: u64 = 64 * 1024 * 1024;

fn invalid(key: &str) -> PeError {
    PeError::Invalid(format!("argumento inválido: {key}"))
}

fn list<T>(a: &Value, key: &str, convert: impl Fn(&Value) -> Option<T>) -> PeResult<Vec<T>> {
    a[key].as_array().ok_or_else(|| invalid(key))?.iter()
        .map(|item| convert(item).ok_or_else(|| invalid(key))).collect()
}

/// Coordenadas planas: un resto indicaría un punto truncado y no se ignora.
fn points(a: &Value, key: &str, dim: usize) -> PeResult<Vec<f64>> {
    let values = list(a, key, Value::as_f64)?;
    if values.len() % dim != 0 {
        return Err(invalid(key));
    }
    Ok(values)
}

fn number(a: &Value, key: &str) -> PeResult<f64> {
    a[key].as_f64().filter(|v| v.is_finite()).ok_or_else(|| invalid(key))
}

fn dimension(a: &Value) -> PeResult<usize> {
    match a["dim"].as_u64() {
        Some(2) => Ok(2),
        Some(3) => Ok(3),
        _ => Err(invalid("dim")),
    }
}

fn run(job: &str, a: &Value) -> PeResult<Value> {
    let cancel = Cancel(None);
    match job {
        "transform3" => {
            let xyz = list(a, "xyz", Value::as_f64)?;
            let m = list(a, "m", Value::as_f64)?;
            let mut out = vec![0.; xyz.len()];
            transform3(&xyz, &m, &mut out, &cancel)?;
            Ok(json!({"xyz": out}))
        }
        "nearest" => {
            let dim = dimension(a)?;
            let q = points(a, "consulta", dim)?;
            let r = points(a, "referencia", dim)?;
            let mut idx = vec![-1; q.len() / dim];
            let mut dist = vec![f64::NAN; idx.len()];
            nearest(&q, &r, dim, number(a, "max_dist")?, &mut idx, &mut dist, &cancel)?;
            Ok(json!({"indice": idx, "distancia": dist}))
        }
        "match" => {
            let dim = dimension(a)?;
            let expected = points(a, "esperados", dim)?;
            let observed = points(a, "observados", dim)?;
            let mut out = vec![-1; expected.len() / dim];
            match_points(&expected, &observed, dim, number(a, "tol")?, &mut out, &cancel)?;
            Ok(json!({"pareja": out}))
        }
        "duplicates" => {
            let dim = dimension(a)?;
            let puntos = points(a, "puntos", dim)?;
            let mut out = vec![0; puntos.len() / dim];
            duplicates(&puntos, dim, number(a, "tol")?, &mut out, &cancel)?;
            Ok(json!({"grupo": out}))
        }
        "segment_contacts" => {
            let segments = points(a, "segmentos", 4)?;
            let contacts = segment_contacts(&segments, number(a, "eps")?, &cancel)?;
            Ok(json!({"contactos": contacts.into_iter().flat_map(|(i,j,k)| [i,j,k]).collect::<Vec<_>>() }))
        }
        "components" => {
            let nodes = a["nodos"].as_u64().filter(|&n| n <= MAX_LINE / 8)
                .ok_or_else(|| invalid("nodos"))? as usize;
            let edges = list(a, "aristas", |v| i32::try_from(v.as_i64()?).ok())?;
            if edges.len() % 2 != 0 {
                return Err(invalid("aristas"));
            }
            let mut comp = vec![0; nodes];
            let mut degree = vec![0; nodes];
            components(nodes, &edges, &mut comp, &mut degree)?;
            Ok(json!({"componente": comp, "grado": degree}))
        }
        _ => unreachable!(),
    }
}

fn fail(id: &Value, code: &str, error: &str) -> Value {
    json!({"pe": PROTOCOL, "id": id, "codigo": code, "error": error})
}

fn respond(req: &Value) -> Value {
    let id = &req["id"];
    if req["pe"].as_u64() != Some(PROTOCOL.into()) {
        return fail(id, "VERSION_INCOMPATIBLE", "contrato distinto de pe=1");
    }
    let args = &req["args"];
    let job = req["trabajo"].as_str().unwrap_or_default();
    if job == "hola" {
        if args["pe_min"].as_u64().unwrap_or(1) > 1 || args["pe_max"].as_u64().unwrap_or(1) < 1 {
            return fail(id, "VERSION_INCOMPATIBLE", "sólo contrato pe=1");
        }
        return json!({"pe": PROTOCOL, "id": id, "codigo": "OK", "resultado":
            {"motor": "rust_geometry_engine", "version": VERSION, "pe": PROTOCOL, "capacidades": JOBS}});
    }
    if !JOBS.contains(&job) {
        return fail(id, "NO_SOPORTADO", "trabajo desconocido");
    }
    let started = Instant::now();
    // Un pánico no debe dejar al cliente sin respuesta correlacionada.
    let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(job, args)));
    let Ok(outcome) = outcome else {
        return fail(id, "ERROR_INTERNO", "pánico en el motor");
    };
    match outcome {
        Ok(result) => json!({"pe": PROTOCOL, "id": id, "codigo": "OK", "resultado": result,
            "telemetria": {"motor_ms": started.elapsed().as_secs_f64() * 1000.}}),
        Err(PeError::Invalid(msg)) => fail(id, "INVALIDO", &msg),
        Err(PeError::Cancelled) => fail(id, "CANCELADO", "trabajo cancelado"),
    }
}

fn main() -> io::Result<()> {
    let mut input = io::stdin().lock();
    let mut output = io::BufWriter::new(io::stdout().lock());
    let mut line = Vec::new();
    loop {
        line.clear();
        if input.by_ref().take(MAX_LINE + 1).read_until(b'\n', &mut line)? == 0 {
            return Ok(());
        }
        if line.len() as u64 > MAX_LINE {
            writeln!(output, "{}", fail(&Value::Null, "DEMASIADO_GRANDE", "petición supera el límite"))?;
            return output.flush();
        }
        let response = match serde_json::from_slice::<Value>(&line) {
            Ok(req) if req["trabajo"] == "salir" => return Ok(()),
            Ok(req) => respond(&req),
            Err(_) => fail(&Value::Null, "INVALIDO", "JSON ilegible"),
        };
        writeln!(output, "{response}")?;
        output.flush()?;
    }
}
