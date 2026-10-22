package runsignal

import (
	"errors"
	"fmt"
	"regexp"
	"strings"
	"unicode"

	"golang.org/x/text/unicode/norm"
)

var namePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9._-]{0,127}$`)

// ErrInvalidName is returned for workload names that are not 1-128 lowercase letters, digits, '.', '_' or '-'
// starting with a letter or digit.
var ErrInvalidName = errors.New("runsignal: invalid workload name")

func validateName(name string) error {
	if !namePattern.MatchString(name) {
		return fmt.Errorf("%w %q: use 1-128 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit", ErrInvalidName, name)
	}
	return nil
}

// Letters that do not decompose into a base letter and a combining mark.
var transliterations = map[rune]string{
	'ı': "i", 'ß': "ss", 'ø': "o", 'Ø': "O", 'æ': "ae", 'Æ': "Ae", 'đ': "d", 'Đ': "D", 'ł': "l", 'Ł': "L",
}

// ToWorkloadName turns an arbitrary job identifier, such as "InvoiceJobs.GenerateInvoices" or
// "Reports/Daily Sales", into a valid workload name such as "invoice-jobs.generate-invoices" or
// "reports-daily-sales". Every RunSignal SDK derives names the same way (spec/conformance/workload-names.json).
func ToWorkloadName(identifier string) (string, error) {
	if strings.TrimSpace(identifier) == "" {
		return "", fmt.Errorf("%w: the identifier is empty", ErrInvalidName)
	}

	// "e-Arşiv gönderimi" -> "e-Arsiv gonderimi", so names stay readable instead of losing letters.
	var ascii strings.Builder
	for _, r := range norm.NFD.String(identifier) {
		if unicode.Is(unicode.Mn, r) {
			continue
		}
		if t, ok := transliterations[r]; ok {
			ascii.WriteString(t)
		} else {
			ascii.WriteRune(r)
		}
	}
	runes := []rune(ascii.String())

	var b strings.Builder
	last := func() byte {
		s := b.String()
		if len(s) == 0 {
			return 0
		}
		return s[len(s)-1]
	}
	for i, r := range runes {
		switch {
		case r >= 'A' && r <= 'Z':
			// Break camel case: "GenerateInvoices" -> "generate-invoices", "HTTPSync" -> "http-sync".
			var prev, next rune
			if i > 0 {
				prev = runes[i-1]
			}
			if i+1 < len(runes) {
				next = runes[i+1]
			}
			l := last()
			if b.Len() > 0 && l != '-' && l != '.' && l != '_' &&
				(isLower(prev) || isDigit(prev) || (isUpper(prev) && isLower(next))) {
				b.WriteByte('-')
			}
			b.WriteRune(unicode.ToLower(r))
		case isLower(r) || isDigit(r) || r == '.' || r == '_':
			b.WriteRune(r)
		default:
			if b.Len() > 0 && last() != '-' {
				b.WriteByte('-')
			}
		}
	}

	name := strings.Trim(b.String(), "-._")
	if len(name) > 128 {
		name = strings.TrimRight(name[:128], "-._")
	}
	if name == "" {
		return "", fmt.Errorf("%w: %q contains no letters or digits to name a workload after", ErrInvalidName, identifier)
	}
	return name, nil
}

func isLower(r rune) bool { return r >= 'a' && r <= 'z' }
func isUpper(r rune) bool { return r >= 'A' && r <= 'Z' }
func isDigit(r rune) bool { return r >= '0' && r <= '9' }
