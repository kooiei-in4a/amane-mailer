import { createHash } from 'node:crypto';

export const INCLUDED_FIELDS = new Set([
  'source_service',
  'purpose',
  'to',
  'cc',
  'bcc',
  'subject',
  'html_body',
  'text_body',
  'reply_to',
  'metadata',
  'attachments',
]);

// attachments is included with a special projection (ADR 0022 D-03): absent or an empty array
// omits the field entirely from the hash document; a non-empty array is re-projected to exactly
// file_name (NFC), content_type, byte_length, content_sha256, and a zero-based order generated
// from array position -- never the raw content_base64 or an unverified declared content_type.
const ATTACHMENTS_FIELD_NAME = 'attachments';

// to/cc/bcc follow the same omission rule (ADR 0023 D-02): unspecified, null, and an empty
// array are all equivalent and the role is omitted from the hash document entirely (no
// "to":null or "to":[] ever appears; a CC-only request has no "to" key at all). A non-empty
// role is re-projected to the validated canonical recipient value (trimmed email;
// whitespace-only display_name treated as absent) -- not the raw request bytes -- so
// equivalent-but-differently-formatted submissions hash identically.
const RECIPIENT_FIELD_NAMES = ['to', 'cc', 'bcc'];

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

function projectAttachments(attachments) {
  return attachments.map((attachment, order) => ({
    file_name: attachment.file_name.normalize('NFC'),
    content_type: attachment.content_type,
    byte_length: attachment.byte_length,
    content_sha256: attachment.content_sha256,
    order,
  }));
}

function projectRecipientRole(role) {
  if (!role || role.length === 0) {
    return null;
  }
  return role.map((recipient) => {
    const entry = { email: recipient.email.trim() };
    const displayName = recipient.display_name;
    if (displayName != null && displayName.trim() !== '') {
      entry.display_name = displayName;
    }
    return entry;
  });
}

export function buildDeliveryPayloadJson(request, attachments = null) {
  const filtered = {};
  for (const key of Object.keys(request)) {
    if (INCLUDED_FIELDS.has(key) && key !== ATTACHMENTS_FIELD_NAME && !RECIPIENT_FIELD_NAMES.includes(key)) {
      filtered[key] = request[key];
    }
  }
  for (const fieldName of RECIPIENT_FIELD_NAMES) {
    if (Object.prototype.hasOwnProperty.call(request, fieldName)) {
      const projected = projectRecipientRole(request[fieldName]);
      if (projected !== null) {
        filtered[fieldName] = projected;
      }
    }
  }
  if (attachments && attachments.length > 0) {
    filtered[ATTACHMENTS_FIELD_NAME] = projectAttachments(attachments);
  }

  const properties = Object.keys(filtered)
    .sort()
    .map((key) => `${escapeJsonString(key)}:${canonicalize(filtered[key])}`);
  return `{${properties.join(',')}}`;
}

export function computeSha256Hex(jsonValue) {
  const canonicalJson = canonicalize(jsonValue);
  return createHash('sha256').update(canonicalJson, 'utf8').digest('hex');
}

export function computeDeliveryPayloadSha256Hex(request, attachments = null) {
  const deliveryJson = buildDeliveryPayloadJson(request, attachments);
  return computeSha256Hex(JSON.parse(deliveryJson));
}
