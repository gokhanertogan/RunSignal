use unicode_normalization::char::is_combining_mark;
use unicode_normalization::UnicodeNormalization;

use crate::Error;

/// Returns an error unless `name` is 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or
/// digit.
pub fn validate_name(name: &str) -> Result<(), Error> {
    let bytes = name.as_bytes();
    let valid = !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && !bytes[0].is_ascii_uppercase()
        && bytes.iter().all(|&b| {
            b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'.' || b == b'_' || b == b'-'
        });
    if valid {
        Ok(())
    } else {
        Err(Error::InvalidName(format!(
            "workload name '{name}' is invalid: use 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit"
        )))
    }
}

// Letters that do not decompose into a base letter and a combining mark.
fn transliterate(c: char) -> Option<&'static str> {
    Some(match c {
        'ı' => "i",
        'ß' => "ss",
        'ø' => "o",
        'Ø' => "O",
        'æ' => "ae",
        'Æ' => "Ae",
        'đ' => "d",
        'Đ' => "D",
        'ł' => "l",
        'Ł' => "L",
        _ => return None,
    })
}

/// Turns an arbitrary job identifier, such as `InvoiceJobs::generate_invoices` or `Reports/Daily Sales`, into a
/// valid workload name such as `invoice-jobs-generate_invoices` or `reports-daily-sales`. Every RunSignal SDK
/// derives names the same way (spec/conformance/workload-names.json).
pub fn to_workload_name(identifier: &str) -> Result<String, Error> {
    if identifier.trim().is_empty() {
        return Err(Error::InvalidName("the identifier is empty".into()));
    }

    // "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
    let mut ascii = String::with_capacity(identifier.len());
    for c in identifier.nfd().filter(|c| !is_combining_mark(*c)) {
        match transliterate(c) {
            Some(t) => ascii.push_str(t),
            None => ascii.push(c),
        }
    }
    let chars: Vec<char> = ascii.chars().collect();

    let mut out = String::with_capacity(chars.len() + 8);
    for (i, &c) in chars.iter().enumerate() {
        if c.is_ascii_uppercase() {
            // Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
            let prev = if i > 0 { chars[i - 1] } else { '\0' };
            let next = chars.get(i + 1).copied().unwrap_or('\0');
            let last = out.chars().last();
            if !out.is_empty()
                && !matches!(last, Some('-' | '.' | '_'))
                && (prev.is_ascii_lowercase()
                    || prev.is_ascii_digit()
                    || (prev.is_ascii_uppercase() && next.is_ascii_lowercase()))
            {
                out.push('-');
            }
            out.push(c.to_ascii_lowercase());
        } else if c.is_ascii_lowercase() || c.is_ascii_digit() || c == '.' || c == '_' {
            out.push(c);
        } else if !out.is_empty() && !out.ends_with('-') {
            out.push('-');
        }
    }

    let separators: &[char] = &['-', '.', '_'];
    let mut name = out.trim_matches(separators).to_string();
    if name.len() > 128 {
        name = name[..128].trim_end_matches(separators).to_string();
    }
    if name.is_empty() {
        return Err(Error::InvalidName(format!(
            "'{identifier}' contains no letters or digits to name a workload after"
        )));
    }
    Ok(name)
}
