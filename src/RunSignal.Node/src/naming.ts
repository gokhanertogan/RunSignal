const NAME_PATTERN = /^[a-z0-9][a-z0-9._-]{0,127}$/;

/** Thrown for workload names that are not 1-128 lowercase letters, digits, '.', '_' or '-'. */
export class InvalidWorkloadNameError extends Error {
  override name = 'InvalidWorkloadNameError';
}

export function validateName(name: string): void {
  if (!NAME_PATTERN.test(name)) {
    throw new InvalidWorkloadNameError(
      `Workload name '${name}' is invalid. Use 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.`,
    );
  }
}

// Letters that do not decompose into a base letter and a combining mark.
const TRANSLITERATIONS: Record<string, string> = {
  ı: 'i', ß: 'ss', ø: 'o', Ø: 'O', æ: 'ae', Æ: 'Ae', đ: 'd', Đ: 'D', ł: 'l', Ł: 'L',
};

const isLower = (c: string | undefined) => c !== undefined && c >= 'a' && c <= 'z';
const isUpper = (c: string | undefined) => c !== undefined && c >= 'A' && c <= 'Z';
const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';

/**
 * Turns an arbitrary job identifier, such as `InvoiceJobs.GenerateInvoices` or `Reports/Daily Sales`, into a
 * valid workload name such as `invoice-jobs.generate-invoices` or `reports-daily-sales`. Every RunSignal SDK
 * derives names the same way (spec/conformance/workload-names.json).
 */
export function toWorkloadName(identifier: string): string {
  if (identifier.trim() === '') {
    throw new InvalidWorkloadNameError('The identifier is empty.');
  }

  // "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
  const ascii = [...identifier.normalize('NFD').replace(/\p{Mn}/gu, '')]
    .map((c) => TRANSLITERATIONS[c] ?? c)
    .join('');
  const chars = [...ascii];

  let out = '';
  chars.forEach((c, i) => {
    if (isUpper(c)) {
      // Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
      const prev = chars[i - 1];
      const next = chars[i + 1];
      const last = out.at(-1);
      if (out.length > 0 && last !== '-' && last !== '.' && last !== '_' &&
          (isLower(prev) || isDigit(prev) || (isUpper(prev) && isLower(next)))) {
        out += '-';
      }
      out += c.toLowerCase();
    } else if (isLower(c) || isDigit(c) || c === '.' || c === '_') {
      out += c;
    } else if (out.length > 0 && out.at(-1) !== '-') {
      out += '-';
    }
  });

  let name = out.replace(/^[-._]+|[-._]+$/g, '');
  if (name.length > 128) {
    name = name.slice(0, 128).replace(/[-._]+$/, '');
  }
  if (name === '') {
    throw new InvalidWorkloadNameError(`'${identifier}' contains no letters or digits to name a workload after.`);
  }
  return name;
}
