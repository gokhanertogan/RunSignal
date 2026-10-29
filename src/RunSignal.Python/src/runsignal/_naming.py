from __future__ import annotations

import re
import unicodedata

_NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")

# Letters that do not decompose into a base letter and a combining mark.
_TRANSLITERATIONS = {
    "ı": "i", "ß": "ss", "ø": "o", "Ø": "O", "æ": "ae", "Æ": "Ae", "đ": "d", "Đ": "D", "ł": "l", "Ł": "L",
}


class InvalidWorkloadNameError(ValueError):
    """Raised for workload names that are not 1-128 lowercase letters, digits, '.', '_' or '-'."""


def validate_name(name: str) -> None:
    if not isinstance(name, str) or not _NAME_PATTERN.match(name):
        raise InvalidWorkloadNameError(
            f"Workload name '{name}' is invalid. Use 1-128 lowercase letters, digits, '.', '_' or '-', "
            "starting with a letter or digit."
        )


def _is_lower(c: str | None) -> bool:
    return c is not None and "a" <= c <= "z"


def _is_upper(c: str | None) -> bool:
    return c is not None and "A" <= c <= "Z"


def _is_digit(c: str | None) -> bool:
    return c is not None and "0" <= c <= "9"


def to_workload_name(identifier: str) -> str:
    """
    Turns an arbitrary job identifier, such as ``InvoiceJobs.GenerateInvoices`` or ``Reports/Daily Sales``, into a
    valid workload name such as ``invoice-jobs.generate-invoices`` or ``reports-daily-sales``. Every RunSignal SDK
    derives names the same way (spec/conformance/workload-names.json).
    """
    if not identifier.strip():
        raise InvalidWorkloadNameError("The identifier is empty.")

    # "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
    decomposed = unicodedata.normalize("NFD", identifier)
    chars = [_TRANSLITERATIONS.get(c, c) for c in decomposed if unicodedata.category(c) != "Mn"]
    chars = list("".join(chars))

    out: list[str] = []
    for i, c in enumerate(chars):
        if _is_upper(c):
            # Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
            prev = chars[i - 1] if i > 0 else None
            nxt = chars[i + 1] if i + 1 < len(chars) else None
            if out and out[-1] not in "-._" and (
                _is_lower(prev) or _is_digit(prev) or (_is_upper(prev) and _is_lower(nxt))
            ):
                out.append("-")
            out.append(c.lower())
        elif _is_lower(c) or _is_digit(c) or c in "._":
            out.append(c)
        elif out and out[-1] != "-":
            out.append("-")

    name = "".join(out).strip("-._")
    if len(name) > 128:
        name = name[:128].rstrip("-._")
    if not name:
        raise InvalidWorkloadNameError(f"'{identifier}' contains no letters or digits to name a workload after.")
    return name
