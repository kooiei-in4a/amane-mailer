export const MailerErrorCode = Object.freeze({
  InvalidRequest: 'INVALID_REQUEST',
  RequestTooLarge: 'REQUEST_TOO_LARGE',
  TooManyRecipients: 'TOO_MANY_RECIPIENTS',
  IdempotencyConflict: 'IDEMPOTENCY_CONFLICT',
  InvalidMetadata: 'INVALID_METADATA',
  Unauthorized: 'UNAUTHORIZED',
  AuthenticationRateLimited: 'AUTHENTICATION_RATE_LIMITED',
  MailerTemporarilyUnavailable: 'MAILER_TEMPORARILY_UNAVAILABLE',
  NotFound: 'NOT_FOUND',
});

export const MailRequestAcceptanceStatus = Object.freeze({
  Accepted: 'accepted',
  AlreadyAccepted: 'already_accepted',
});

export class MailerError extends Error {
  constructor(message, { statusCode, code, retryable = false, body = undefined } = {}) {
    super(message);
    this.name = 'MailerError';
    this.statusCode = statusCode;
    this.code = code;
    this.retryable = retryable;
    this.body = body;
  }
}

export class MailerValidationError extends MailerError {
  constructor(message, options = {}) {
    super(message, options);
    this.name = 'MailerValidationError';
  }
}

export class MailerIdempotencyConflictError extends MailerError {
  constructor(message, options = {}) {
    super(message, options);
    this.name = 'MailerIdempotencyConflictError';
  }
}

export class MailerRetryableError extends MailerError {
  constructor(message, options = {}) {
    super(message, { ...options, retryable: true });
    this.name = 'MailerRetryableError';
  }
}

export class MailRequestAcceptedResponse {
  constructor({ mailRequestId, status }) {
    this.mailRequestId = mailRequestId;
    this.status = status;
  }

  get isFirstAcceptance() {
    return this.status === MailRequestAcceptanceStatus.Accepted;
  }

  get isIdempotentResend() {
    return this.status === MailRequestAcceptanceStatus.AlreadyAccepted;
  }
}

export function toRetryableTransportError(error) {
  if (error instanceof MailerRetryableError) {
    return error;
  }

  const message = error instanceof Error ? error.message : 'Mailer request failed.';
  return new MailerRetryableError(message);
}

export function parseMailerError(statusCode, bodyText) {
  let body;
  try {
    body = JSON.parse(bodyText);
  } catch {
    return new MailerError(`Mailer returned HTTP ${statusCode}.`, { statusCode, body: bodyText });
  }

  const code = body.code;
  const message = body.message ?? `Mailer returned ${code ?? statusCode}.`;
  const retryable = body.retryable === true;

  if (statusCode === 409 && code === MailerErrorCode.IdempotencyConflict) {
    return new MailerIdempotencyConflictError(message, { statusCode, code, body });
  }

  if (statusCode === 422) {
    return new MailerValidationError(message, { statusCode, code, body });
  }

  if (statusCode === 503 || retryable) {
    return new MailerRetryableError(message, { statusCode, code, body });
  }

  return new MailerError(message, { statusCode, code, retryable, body });
}
