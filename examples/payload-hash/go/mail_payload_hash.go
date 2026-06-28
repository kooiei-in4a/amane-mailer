package mailpayloadhash

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strings"
)

var includedFields = map[string]struct{}{
	"source_service": {},
	"purpose":        {},
	"to":             {},
	"subject":        {},
	"html_body":      {},
	"text_body":      {},
	"reply_to":       {},
	"metadata":       {},
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

func canonicalizeObject(value map[string]any) (string, error) {
	keys := make([]string, 0, len(value))
	for key := range value {
		keys = append(keys, key)
	}
	sort.Strings(keys)

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
	filtered := make(map[string]any)
	for key, value := range request {
		if _, ok := includedFields[key]; ok {
			filtered[key] = value
		}
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
	deliveryJSON, err := BuildDeliveryPayloadJSON(request)
	if err != nil {
		return "", err
	}
	var parsed any
	if err := json.Unmarshal([]byte(deliveryJSON), &parsed); err != nil {
		return "", err
	}
	return ComputeSHA256Hex(parsed)
}
