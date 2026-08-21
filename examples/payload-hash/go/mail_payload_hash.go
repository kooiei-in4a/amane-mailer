package mailpayloadhash

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strings"
	"unicode/utf16"
)

var includedFields = map[string]struct{}{
	"source_service": {},
	"purpose":        {},
	"to":             {},
	"cc":             {},
	"bcc":            {},
	"subject":        {},
	"html_body":      {},
	"text_body":      {},
	"reply_to":       {},
	"metadata":       {},
	"attachments":    {},
}

// attachmentsFieldName is included with a special projection (ADR 0022 D-03): absent or an
// empty slice omits the field entirely from the hash document; a non-empty slice is
// re-projected to exactly file_name, content_type, byte_length, content_sha256, and a
// zero-based order generated from slice position -- never the raw content_base64 or an
// unverified declared content_type.
//
// Unlike the C#/Python/TypeScript reference implementations (the ADR 0022 D-03 parity set),
// this Go example does not perform Unicode NFC normalization on file_name: the Go standard
// library has no built-in normalizer, and adding golang.org/x/text as a dependency is out of
// scope for this dependency-free reference file. Callers must pass an already NFC-normalized
// file_name (the shared test vectors use precomposed Japanese text, so this does not affect
// vector parity here).
const attachmentsFieldName = "attachments"

// recipientFieldNames follow the same omission rule as attachmentsFieldName (ADR 0023 D-02):
// unspecified, null, and an empty slice are all equivalent and the role is omitted from the
// hash document entirely (no "to":null or "to":[] ever appears; a CC-only request has no "to"
// key at all). A non-empty role is re-projected to the validated canonical recipient value
// (trimmed email; whitespace-only display_name treated as absent) -- not the raw request bytes
// -- so equivalent-but-differently-formatted submissions hash identically.
var recipientFieldNames = []string{"to", "cc", "bcc"}

func isRecipientField(key string) bool {
	for _, name := range recipientFieldNames {
		if key == name {
			return true
		}
	}
	return false
}

func projectRecipientRole(role any) any {
	items, ok := role.([]any)
	if !ok || len(items) == 0 {
		return nil
	}

	projected := make([]any, 0, len(items))
	for _, item := range items {
		recipient, ok := item.(map[string]any)
		if !ok {
			continue
		}

		entry := map[string]any{}
		if email, ok := recipient["email"].(string); ok {
			entry["email"] = strings.TrimSpace(email)
		}
		if displayName, ok := recipient["display_name"].(string); ok && strings.TrimSpace(displayName) != "" {
			entry["display_name"] = displayName
		}
		projected = append(projected, entry)
	}
	return projected
}

// Attachment is the verified (not Consumer-declared) shape used for the hash projection.
type Attachment struct {
	FileName      string
	ContentType   string
	ByteLength    int64
	ContentSHA256 string
}

func projectAttachments(attachments []Attachment) []any {
	projected := make([]any, 0, len(attachments))
	for order, attachment := range attachments {
		projected = append(projected, map[string]any{
			"file_name":      attachment.FileName,
			"content_type":   attachment.ContentType,
			"byte_length":    attachment.ByteLength,
			"content_sha256": attachment.ContentSHA256,
			"order":          order,
		})
	}
	return projected
}

func EscapeJSONString(value string) string {
	var builder strings.Builder
	builder.WriteByte('"')
	for _, character := range value {
		switch character {
		case '"':
			builder.WriteString(`\"`)
		case '\\':
			builder.WriteString(`\\`)
		case '\b':
			builder.WriteString(`\b`)
		case '\f':
			builder.WriteString(`\f`)
		case '\n':
			builder.WriteString(`\n`)
		case '\r':
			builder.WriteString(`\r`)
		case '\t':
			builder.WriteString(`\t`)
		default:
			if character < 0x20 {
				builder.WriteString(fmt.Sprintf(`\u%04x`, character))
			} else {
				builder.WriteRune(character)
			}
		}
	}
	builder.WriteByte('"')
	return builder.String()
}

func Canonicalize(value any) (string, error) {
	switch typed := value.(type) {
	case nil:
		return "null", nil
	case bool:
		if typed {
			return "true", nil
		}
		return "false", nil
	case string:
		return EscapeJSONString(typed), nil
	case json.Number:
		return canonicalizeNumber(typed)
	case float64:
		return canonicalizeFloat(typed)
	case int:
		return fmt.Sprintf("%d", typed), nil
	case int64:
		return fmt.Sprintf("%d", typed), nil
	case []any:
		parts := make([]string, 0, len(typed))
		for _, item := range typed {
			part, err := Canonicalize(item)
			if err != nil {
				return "", err
			}
			parts = append(parts, part)
		}
		return "[" + strings.Join(parts, ",") + "]", nil
	case map[string]any:
		return canonicalizeObject(typed)
	default:
		return "", fmt.Errorf("unsupported JSON value type %T", value)
	}
}

func compareOrdinal(a, b string) int {
	ua := utf16.Encode([]rune(a))
	ub := utf16.Encode([]rune(b))
	limit := len(ua)
	if len(ub) < limit {
		limit = len(ub)
	}
	for index := 0; index < limit; index++ {
		if ua[index] != ub[index] {
			if ua[index] < ub[index] {
				return -1
			}
			return 1
		}
	}
	if len(ua) == len(ub) {
		return 0
	}
	if len(ua) < len(ub) {
		return -1
	}
	return 1
}

func sortKeysOrdinal(keys []string) {
	sort.Slice(keys, func(i, j int) bool {
		return compareOrdinal(keys[i], keys[j]) < 0
	})
}

func canonicalizeObject(value map[string]any) (string, error) {
	keys := make([]string, 0, len(value))
	for key := range value {
		keys = append(keys, key)
	}
	sortKeysOrdinal(keys)

	parts := make([]string, 0, len(keys))
	for _, key := range keys {
		canonicalValue, err := Canonicalize(value[key])
		if err != nil {
			return "", err
		}
		parts = append(parts, EscapeJSONString(key)+":"+canonicalValue)
	}
	return "{" + strings.Join(parts, ",") + "}", nil
}

func canonicalizeNumber(number json.Number) (string, error) {
	if integer, err := number.Int64(); err == nil {
		return fmt.Sprintf("%d", integer), nil
	}
	return number.String(), nil
}

func canonicalizeFloat(value float64) (string, error) {
	if value == math.Trunc(value) && value >= -1<<53 && value <= 1<<53 {
		return fmt.Sprintf("%.0f", value), nil
	}
	return strings.TrimRight(strings.TrimRight(fmt.Sprintf("%g", value), "0"), "."), nil
}

func BuildDeliveryPayloadJSON(request map[string]any) (string, error) {
	return BuildDeliveryPayloadJSONWithAttachments(request, nil)
}

// BuildDeliveryPayloadJSONWithAttachments builds the canonical hash document, projecting
// attachments (ADR 0022 D-03) when non-empty. The raw "attachments" key on request, if any,
// is never used directly (it may carry content_base64 and an unverified declared
// content_type); pass the verified attachments explicitly instead.
func BuildDeliveryPayloadJSONWithAttachments(request map[string]any, attachments []Attachment) (string, error) {
	filtered := make(map[string]any)
	for key, value := range request {
		if key == attachmentsFieldName || isRecipientField(key) {
			continue
		}
		if _, ok := includedFields[key]; ok {
			filtered[key] = value
		}
	}
	for _, fieldName := range recipientFieldNames {
		if value, ok := request[fieldName]; ok {
			if projected := projectRecipientRole(value); projected != nil {
				filtered[fieldName] = projected
			}
		}
	}
	if len(attachments) > 0 {
		filtered[attachmentsFieldName] = projectAttachments(attachments)
	}
	return canonicalizeObject(filtered)
}

func ComputeSHA256Hex(value any) (string, error) {
	canonical, err := Canonicalize(value)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256([]byte(canonical))
	return hex.EncodeToString(sum[:]), nil
}

func ComputeDeliveryPayloadSHA256Hex(request map[string]any) (string, error) {
	return ComputeDeliveryPayloadSHA256HexWithAttachments(request, nil)
}

func ComputeDeliveryPayloadSHA256HexWithAttachments(request map[string]any, attachments []Attachment) (string, error) {
	deliveryJSON, err := BuildDeliveryPayloadJSONWithAttachments(request, attachments)
	if err != nil {
		return "", err
	}
	var parsed any
	if err := json.Unmarshal([]byte(deliveryJSON), &parsed); err != nil {
		return "", err
	}
	return ComputeSHA256Hex(parsed)
}
