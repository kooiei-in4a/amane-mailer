const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SOURCE_SERVICE_PATTERN = /^[a-z0-9][a-z0-9_-]{1,62}$/;
const FORBIDDEN_METADATA_KEY_PATTERN = /token|password|secret|url/i;
const SCHEDULED_AT_OFFSET_PATTERN = /(Z|[+-]\d{2}:\d{2})$/;
const SCHEDULED_AT_DATE_PREFIX_PATTERN = /^(\d{4})-(\d{2})-(\d{2})T/;

export class MailRequestValidationError extends Error {
  constructor(message) {
    super(message);
    this.name = 'MailRequestValidationError';
  }
}

export function assertUuid(value, fieldName) {
  if (typeof value !== 'string' || !UUID_PATTERN.test(value)) {
    throw new MailRequestValidationError(`${fieldName} must be a UUID string.`);
  }
}

export function assertSourceService(value) {
  if (typeof value !== 'string' || !SOURCE_SERVICE_PATTERN.test(value)) {
    throw new MailRequestValidationError(
      'source_service must match ^[a-z0-9][a-z0-9_-]{1,62}$.',
    );
  }
}

export function assertRecipient(recipient) {
  if (!recipient || typeof recipient !== 'object' || typeof recipient.email !== 'string') {
    throw new MailRequestValidationError('to[0].email is required.');
  }
  if (recipient.email.length === 0) {
    throw new MailRequestValidationError('to[0].email must not be empty.');
  }
}

export function assertMetadata(metadata) {
  if (metadata === null || metadata === undefined) {
    return;
  }
  if (typeof metadata !== 'object' || Array.isArray(metadata)) {
    throw new MailRequestValidationError('metadata must be an object when provided.');
  }

  for (const [key, value] of Object.entries(metadata)) {
    if (FORBIDDEN_METADATA_KEY_PATTERN.test(key)) {
      throw new MailRequestValidationError(
        `metadata key "${key}" is rejected (token/password/secret/url).`,
      );
    }
    if (typeof value !== 'string') {
      throw new MailRequestValidationError('metadata values must be strings.');
    }
  }
}

function hasValidCalendarDatePrefix(value) {
  const match = SCHEDULED_AT_DATE_PREFIX_PATTERN.exec(value);
  if (!match) {
    return true;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const probe = new Date(Date.UTC(year, month - 1, day));
  return (
    probe.getUTCFullYear() === year
    && probe.getUTCMonth() === month - 1
    && probe.getUTCDate() === day
  );
}

export function assertScheduledAt(value) {
  if (value === null || value === undefined) {
    return;
  }
  if (typeof value !== 'string') {
    throw new MailRequestValidationError(
      'scheduled_at must be an ISO-8601 date-time string or null.',
    );
  }
  if (!SCHEDULED_AT_OFFSET_PATTERN.test(value)) {
    throw new MailRequestValidationError(
      'scheduled_at must include a timezone offset or Z.',
    );
  }
  if (Number.isNaN(Date.parse(value)) || !hasValidCalendarDatePrefix(value)) {
    throw new MailRequestValidationError(
      'scheduled_at must be a valid ISO-8601 date-time with timezone offset or Z.',
    );
  }
}

export function validateMailRequestDraft(draft) {
  assertUuid(draft.tenant_id, 'tenant_id');
  assertUuid(draft.mail_request_id, 'mail_request_id');
  assertSourceService(draft.source_service);

  if (typeof draft.purpose !== 'string' || draft.purpose.length === 0) {
    throw new MailRequestValidationError('purpose is required.');
  }

  if (!Array.isArray(draft.to) || draft.to.length === 0) {
    throw new MailRequestValidationError('to must contain at least one recipient.');
  }
  if (draft.to.length > 1) {
    throw new MailRequestValidationError('to must contain at most one recipient.');
  }
  assertRecipient(draft.to[0]);

  if (typeof draft.subject !== 'string' || draft.subject.length === 0) {
    throw new MailRequestValidationError('subject is required.');
  }

  const hasHtmlBody = draft.html_body !== undefined && draft.html_body !== null && draft.html_body !== '';
  const hasTextBody = draft.text_body !== undefined && draft.text_body !== null && draft.text_body !== '';
  if (!hasHtmlBody && !hasTextBody) {
    throw new MailRequestValidationError('At least one of html_body or text_body is required.');
  }

  assertMetadata(draft.metadata);
  if (Object.prototype.hasOwnProperty.call(draft, 'scheduled_at')) {
    assertScheduledAt(draft.scheduled_at);
  }
}
