package io.runsignal;

import java.text.Normalizer;
import java.util.Map;
import java.util.regex.Pattern;

/** Validates workload names and derives them from job identifiers, the same way in every RunSignal SDK. */
public final class WorkloadNames {
  private static final Pattern NAME = Pattern.compile("^[a-z0-9][a-z0-9._-]{0,127}$");
  private static final Pattern MARKS = Pattern.compile("\\p{Mn}+");

  // Letters that do not decompose into a base letter and a combining mark.
  private static final Map<Character, String> TRANSLITERATIONS = Map.of(
      'ı', "i", 'ß', "ss", 'ø', "o", 'Ø', "O", 'æ', "ae", 'Æ', "Ae", 'đ', "d", 'Đ', "D", 'ł', "l", 'Ł', "L");

  private WorkloadNames() {}

  /** Throws {@link IllegalArgumentException} unless {@code name} is 1-128 lowercase letters, digits, '.', '_' or '-'. */
  public static void validate(String name) {
    if (name == null || !NAME.matcher(name).matches()) {
      throw new IllegalArgumentException("Workload name '" + name
          + "' is invalid. Use 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.");
    }
  }

  /**
   * Turns an arbitrary job identifier, such as {@code InvoiceJobs.generateInvoices} or {@code Reports/Daily Sales},
   * into a valid workload name such as {@code invoice-jobs.generate-invoices} or {@code reports-daily-sales}
   * (spec/conformance/workload-names.json).
   */
  public static String toWorkloadName(String identifier) {
    if (identifier == null || identifier.isBlank()) {
      throw new IllegalArgumentException("The identifier is empty.");
    }

    // "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
    String decomposed = MARKS.matcher(Normalizer.normalize(identifier, Normalizer.Form.NFD)).replaceAll("");
    StringBuilder ascii = new StringBuilder(decomposed.length());
    for (char c : decomposed.toCharArray()) {
      ascii.append(TRANSLITERATIONS.getOrDefault(c, String.valueOf(c)));
    }

    String s = ascii.toString();
    StringBuilder out = new StringBuilder(s.length() + 8);
    for (int i = 0; i < s.length(); i++) {
      char c = s.charAt(i);
      if (isUpper(c)) {
        // Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
        char prev = i > 0 ? s.charAt(i - 1) : 0;
        char next = i + 1 < s.length() ? s.charAt(i + 1) : 0;
        char last = out.length() > 0 ? out.charAt(out.length() - 1) : 0;
        if (out.length() > 0 && last != '-' && last != '.' && last != '_'
            && (isLower(prev) || isDigit(prev) || (isUpper(prev) && isLower(next)))) {
          out.append('-');
        }
        out.append((char) (c + ('a' - 'A')));
      } else if (isLower(c) || isDigit(c) || c == '.' || c == '_') {
        out.append(c);
      } else if (out.length() > 0 && out.charAt(out.length() - 1) != '-') {
        out.append('-');
      }
    }

    String name = trim(out.toString());
    if (name.length() > 128) {
      name = trim(name.substring(0, 128));
    }
    if (name.isEmpty()) {
      throw new IllegalArgumentException("'" + identifier + "' contains no letters or digits to name a workload after.");
    }
    return name;
  }

  private static String trim(String s) {
    int start = 0;
    int end = s.length();
    while (start < end && isSeparator(s.charAt(start))) start++;
    while (end > start && isSeparator(s.charAt(end - 1))) end--;
    return s.substring(start, end);
  }

  private static boolean isSeparator(char c) {
    return c == '-' || c == '.' || c == '_';
  }

  private static boolean isLower(char c) {
    return c >= 'a' && c <= 'z';
  }

  private static boolean isUpper(char c) {
    return c >= 'A' && c <= 'Z';
  }

  private static boolean isDigit(char c) {
    return c >= '0' && c <= '9';
  }
}
