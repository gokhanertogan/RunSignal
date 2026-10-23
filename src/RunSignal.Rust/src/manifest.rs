use std::fmt::Write;

use crate::Workload;

/// Writes the manifest exactly as the other SDKs do: two-space indentation, absent values left out.
pub(crate) fn write(service: &str, workloads: &[Workload]) -> String {
    let mut b = String::new();
    let _ = write!(
        b,
        "{{\n  \"version\": 1,\n  \"service\": {},\n  \"workloads\": [",
        string(service)
    );
    for (i, w) in workloads.iter().enumerate() {
        let o = w.options();
        b.push_str(if i == 0 { "\n" } else { ",\n" });
        let _ = write!(b, "    {{\n      \"name\": {}", string(w.name()));
        if let Some(s) = o.get_schedule() {
            let _ = write!(b, ",\n      \"schedule\": {}", string(s));
        }
        if let Some(d) = o.get_max_duration() {
            let _ = write!(
                b,
                ",\n      \"maxDurationSeconds\": {}",
                number(d.as_secs_f64())
            );
        }
        if let Some(d) = o.get_expect_every() {
            let _ = write!(
                b,
                ",\n      \"expectEverySeconds\": {}",
                number(d.as_secs_f64())
            );
        }
        if let Some(n) = o.get_min_processed().filter(|n| *n > 0) {
            let _ = write!(b, ",\n      \"minProcessed\": {n}");
        }
        if let Some(r) = o.get_failure_rate_threshold().filter(|r| *r > 0.0) {
            let _ = write!(b, ",\n      \"failureRateThreshold\": {}", number(r));
        }
        let _ = write!(
            b,
            ",\n      \"severity\": {}\n    }}",
            string(o.get_severity().as_str())
        );
    }
    b.push_str(if workloads.is_empty() {
        "]\n}"
    } else {
        "\n  ]\n}"
    });
    b
}

// 1800.0 -> "1800", 0.2 -> "0.2", as the other SDKs print them.
fn number(v: f64) -> String {
    if v.fract() == 0.0 && v.abs() < 1e15 {
        format!("{}", v as i64)
    } else {
        format!("{v}")
    }
}

fn string(v: &str) -> String {
    let mut s = String::with_capacity(v.len() + 2);
    s.push('"');
    for c in v.chars() {
        match c {
            '"' => s.push_str("\\\""),
            '\\' => s.push_str("\\\\"),
            '\n' => s.push_str("\\n"),
            '\r' => s.push_str("\\r"),
            '\t' => s.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                let _ = write!(s, "\\u{:04x}", c as u32);
            }
            c => s.push(c),
        }
    }
    s.push('"');
    s
}
