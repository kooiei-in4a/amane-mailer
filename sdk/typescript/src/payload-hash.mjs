import { createHash } from 'node:crypto';

export const INCLUDED_FIELDS = new Set([
  'source_service',
  'purpose',
  'to',
  'subject',
  'html_body',
  'text_body',
  'reply_to',
  'metadata',
]);

export function escapeJsonString(value) {
  let result = '"';
  for (const character of value) {
    switch (character) {
      case '"':
        result += '\\"';
        break;
      case '\\':
        result += '\\\\';
        break;
      case '\b':
        result += '\\b';
        break;
      case '\f':
        result += '\\f';
        break;
      case '\n':
        result += '\\n';
        break;
      case '\r':
        result += '\\r';
        break;
      case '\t':
        result += '\\t';
        break;
      default:
        if (character.charCodeAt(0) < 0x20) {
          result += `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`;
        } else {
          result += character;
        }
        break;
    }
  }
  result += '"';
  return result;
}

export function canonicalize(value) {
  if (value === null) {
    return 'null';
  }
  if (typeof value === 'boolean') {
    return value ? 'true' : 'false';
  }
  if (typeof value === 'string') {
    return escapeJsonString(value);
  }
  if (typeof value === 'number') {
    if (Number.isInteger(value)) {
      return value.toString(10);
    }
    return value.toString();
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => canonicalize(item)).join(',')}]`;
  }
  if (typeof value === 'object') {
    const properties = Object.keys(value)
      .sort()
      .map((key) => `${escapeJsonString(key)}:${canonicalize(value[key])}`);
    return `{${properties.join(',')}}`;
  }
  throw new TypeError(`Unsupported JSON value type: ${typeof value}`);
}

export function buildDeliveryPayloadJson(request) {
  const properties = Object.keys(request)
    .filter((key) => INCLUDED_FIELDS.has(key))
    .sort()
    .map((key) => `${escapeJsonString(key)}:${canonicalize(request[key])}`);
  return `{${properties.join(',')}}`;
}

export function computeSha256Hex(jsonValue) {
  const canonicalJson = canonicalize(jsonValue);
  return createHash('sha256').update(canonicalJson, 'utf8').digest('hex');
}

export function computeDeliveryPayloadSha256Hex(request) {
  const deliveryJson = buildDeliveryPayloadJson(request);
  return computeSha256Hex(JSON.parse(deliveryJson));
}
