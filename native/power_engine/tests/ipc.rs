// SPDX-License-Identifier: GPL-3.0-only
//! pe=1 IPC worker protocol, running the real binary.
use serde_json::{json, Value};
use std::io::Write;
use std::process::{Command, Stdio};

fn exchange(lines: &[String]) -> Vec<Value> {
    let mut child = Command::new(env!("CARGO_BIN_EXE_bimbridge-power-engine"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    {
        let mut stdin = child.stdin.take().unwrap();
        for l in lines {
            writeln!(stdin, "{l}").unwrap();
        }
    }
    let out = child.wait_with_output().unwrap();
    assert!(out.status.success());
    String::from_utf8(out.stdout).unwrap().lines().map(|l| serde_json::from_str(l).unwrap()).collect()
}

fn one(job: &str, args: Value) -> Value {
    let r = exchange(&[json!({"pe":1,"id":"t","job":job,"args":args}).to_string()]);
    assert_eq!(r.len(), 1);
    assert_eq!(r[0]["id"], "t");
    r[0].clone()
}

#[test]
fn hello_announces_all_jobs_and_rejects_other_versions() {
    let r = one("hola", json!({"pe_min":1,"pe_max":1}));
    assert_eq!(r["code"], "OK");
    assert_eq!(r["result"]["capacidades"].as_array().unwrap().len(), 6);
    assert_eq!(one("hola", json!({"pe_min":2,"pe_max":3}))["code"], "VERSION_INCOMPATIBLE");
    let bad = exchange(&[json!({"pe":2,"id":"v","job":"hola"}).to_string()]);
    assert_eq!(bad[0]["code"], "VERSION_INCOMPATIBLE");
}

#[test]
fn six_jobs_answer() {
    let t = one("transform3", json!({"xyz":[1.,2.,3.],"m":[1.,0.,0.,5., 0.,1.,0.,0., 0.,0.,1.,0.]}));
    assert_eq!(t["result"]["xyz"][0], 6.0);
    let n = one("nearest", json!({"dim":2,"consulta":[0.,0.,50.,50.],"referencia":[1.,0.],"max_dist":5.}));
    assert_eq!(n["result"]["indice"], json!([0, -1]));
    assert_eq!(n["result"]["distancia"][1], Value::Null, "no neighbour: NaN travels as null");
    let m = one("match", json!({"dim":2,"esperados":[0.,0.],"observados":[0.,0.1],"tol":1.}));
    assert_eq!(m["result"]["pareja"], json!([0]));
    let d = one("duplicates", json!({"dim":3,"puntos":[0.,0.,0.,0.,0.,0.,9.,9.,9.],"tol":0.1}));
    let g = d["result"]["grupo"].as_array().unwrap();
    assert_eq!(g[0], g[1]);
    assert_ne!(g[0], g[2]);
    let s = one("segment_contacts", json!({"segmentos":[0.,0.,2.,2.,0.,2.,2.,0.],"eps":1e-9}));
    assert_eq!(s["code"], "OK");
    assert!(!s["result"]["contactos"].as_array().unwrap().is_empty());
    let c = one("components", json!({"nodos":3,"aristas":[0,1]}));
    assert_eq!(c["result"]["grado"], json!([1, 1, 0]));
}

#[test]
fn invalid_inputs_fail_closed_and_worker_keeps_serving() {
    let cases = [
        ("nearest", json!({"dim":4,"consulta":[],"referencia":[],"max_dist":1.})),
        ("nearest", json!({"dim":2,"consulta":[1.],"referencia":[1.,2.],"max_dist":1.})),
        ("nearest", json!({"dim":2,"consulta":["x",1.],"referencia":[],"max_dist":1.})),
        ("match", json!({"dim":2,"esperados":[0.,0.]})),
        ("transform3", json!({"xyz":[1.,2.],"m":[1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.]})),
        ("transform3", json!({"xyz":[1.,2.,3.],"m":[1.]})),
        ("components", json!({"nodos":2,"aristas":[0,9]})),
        ("components", json!({"nodos":2,"aristas":[0]})),
        ("components", json!({"nodos":2,"aristas":[-1,0]})),
        ("components", json!({"nodos":-1,"aristas":[]})),
        ("segment_contacts", json!({"segmentos":[1.,2.,3.],"eps":0.1})),
    ];
    for (job, args) in cases {
        let r = one(job, args.clone());
        assert_eq!(r["code"], "INVALIDO", "{job} {args}: {r}");
    }
    let r = exchange(&[
        "{not json".into(),
        json!({"pe":1,"id":"a","job":"inventado"}).to_string(),
        json!({"pe":1,"id":"b","job":"components","args":{"nodos":1,"aristas":[]}}).to_string(),
    ]);
    assert_eq!(r[0]["code"], "INVALIDO");
    assert_eq!(r[1]["code"], "NO_SOPORTADO");
    assert_eq!(r[2]["code"], "OK");
    assert_eq!(r[2]["id"], "b");
}

#[test]
fn exit_message_ends_without_response() {
    let r = exchange(&[json!({"pe":1,"id":"x","job":"salir"}).to_string(), json!({"pe":1,"id":"y","job":"hola"}).to_string()]);
    assert!(r.is_empty());
}

#[test]
fn eof_ends_cleanly() {
    assert!(exchange(&[]).is_empty());
}

#[test]
fn oversized_line_is_refused_and_does_not_hang() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_bimbridge-power-engine"))
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().unwrap();
    let mut stdin = child.stdin.take().unwrap();
    let writer = std::thread::spawn(move || {
        // The worker cuts off above 64 MiB: a broken-pipe error on the writer is expected.
        let chunk = vec![b' '; 1024 * 1024];
        for _ in 0..70 {
            if stdin.write_all(&chunk).is_err() { return; }
        }
        let _ = stdin.write_all(b"\n");
    });
    let out = child.wait_with_output().unwrap();
    writer.join().unwrap();
    let r: Value = serde_json::from_str(String::from_utf8(out.stdout).unwrap().lines().next().unwrap()).unwrap();
    assert_eq!(r["code"], "DEMASIADO_GRANDE");
}

#[test]
fn nan_result_travels_as_null_and_non_finite_parameters_are_refused() {
    let n = one("nearest", json!({"dim":2,"consulta":[0.,0.],"referencia":[],"max_dist":1.}));
    assert_eq!(n["result"]["indice"], json!([-1]));
    assert_eq!(n["result"]["distancia"], json!([null]));
    let bad = exchange(&[r#"{"pe":1,"id":"z","job":"nearest","args":{"dim":2,"consulta":[0,0],"referencia":[],"max_dist":null}}"#.into()]);
    assert_eq!(bad[0]["code"], "INVALIDO");
}
