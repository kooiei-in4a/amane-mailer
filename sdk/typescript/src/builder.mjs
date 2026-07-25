import { computeDeliveryPayloadSha256Hex } from './payload-hash.mjs';
import { validateMailRequestDraft } from './validation.mjs';
import { generateMailRequestId } from './uuid.mjs';

function cloneRecipient(recipient) {
  const result = { email: recipient.email };
  if (recipient.display_name !== undefined) {
    result.display_name = recipient.display_name;
  }
  return result;
}

export class MailRequestBuilder {
  static create() {
    return new MailRequestBuilder();
  }

  constructor() {
    this.#fields = {};
    this.#explicitNulls = new Set();
  }

  #fields;
  #explicitNulls;

  tenantId(value) {
    this.#fields.tenant_id = value;
    return this;
  }

  sourceService(value) {
    this.#fields.source_service = value;
    return this;
  }

  mailRequestId(value) {
    this.#fields.mail_request_id = value;
    return this;
  }

  generateMailRequestId() {
    return this.mailRequestId(generateMailRequestId());
  }

  purpose(value) {
    this.#fields.purpose = value;
    return this;
  }

  to(recipient) {
    this.#fields.to = [cloneRecipient(recipient)];
    return this;
  }

  subject(value) {
    this.#fields.subject = value;
    return this;
  }

  htmlBody(value) {
    this.#fields.html_body = value;
    if (value === null) {
      this.#explicitNulls.add('html_body');
    } else {
      this.#explicitNulls.delete('html_body');
    }
    return this;
  }

  textBody(value) {
    this.#fields.text_body = value;
    if (value === null) {
      this.#explicitNulls.add('text_body');
    } else {
      this.#explicitNulls.delete('text_body');
    }
    return this;
  }

  replyTo(value) {
    this.#fields.reply_to = value;
    if (value === null) {
      this.#explicitNulls.add('reply_to');
    } else {
      this.#explicitNulls.delete('reply_to');
    }
    return this;
  }

  metadata(value) {
    this.#fields.metadata = value;
    if (value === null) {
      this.#explicitNulls.add('metadata');
    } else {
      this.#explicitNulls.delete('metadata');
    }
    return this;
  }

  scheduledAt(value) {
    this.#fields.scheduled_at = value;
    if (value === null) {
      this.#explicitNulls.add('scheduled_at');
    } else {
      this.#explicitNulls.delete('scheduled_at');
    }
    return this;
  }

  build() {
    const draft = { ...this.#fields };

    for (const key of ['html_body', 'text_body', 'reply_to', 'metadata', 'scheduled_at']) {
      if (draft[key] === undefined) {
        delete draft[key];
      } else if (draft[key] === null && !this.#explicitNulls.has(key)) {
        delete draft[key];
      }
    }

    validateMailRequestDraft(draft);

    draft.payload_hash = computeDeliveryPayloadSha256Hex({
      ...draft,
      payload_hash: 'placeholder',
    });

    return Object.freeze({ ...draft });
  }
}
