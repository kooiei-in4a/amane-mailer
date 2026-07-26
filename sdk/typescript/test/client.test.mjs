import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';

import { MailRequestBuilder } from '../src/builder.mjs';
import { MailerClient } from '../src/client.mjs';
import {
  MailerIdempotencyConflictError,
  MailerRetryableError,
  MailerValidationError,
  MailRequestAcceptanceStatus,
} from '../src/errors.mjs';
import { generateUuidV7 } from '../src/uuid.mjs';
import { MailRequestValidationError } from '../src/validation.mjs';

function buildSampleRequest() {
  return MailRequestBuilder.create()
    .tenantId('00000000-0000-0000-0000-000000000101')
    .sourceService('example-service')
    .mailRequestId('00000000-0000-0000-0000-000000000201')
    .purpose('FormResponseNotification')
    .to({ email: 'admin@example.com' })
    .subject('New response')
    .textBody('A new response arrived.')
    .build();
}

test('MailRequestBuilder computes payload_hash and validates input', () => {
  const request = buildSampleRequest();
  assert.match(request.payload_hash, /^[0-9a-f]{64}$/);
  assert.equal(request.payload_hash, '7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9');
});

test('MailRequestBuilder omits scheduled_at when unset', () => {
  const request = buildSampleRequest();
  assert.equal(Object.hasOwn(request, 'scheduled_at'), false);
});

test('MailRequestBuilder accepts scheduled_at with Z and offsets', () => {
  for (const value of [
    '2026-08-01T09:00:00Z',
    '2026-08-01T18:00:00+09:00',
    '2026-08-01T00:00:00-05:00',
  ]) {
    const request = MailRequestBuilder.create()
      .tenantId('00000000-0000-0000-0000-000000000101')
      .sourceService('example-service')
      .mailRequestId('00000000-0000-0000-0000-000000000201')
      .purpose('FormResponseNotification')
      .to({ email: 'admin@example.com' })
      .subject('New response')
      .textBody('A new response arrived.')
      .scheduledAt(value)
      .build();
    assert.equal(request.scheduled_at, value);
  }
});

test('MailRequestBuilder rejects timezone-less and invalid scheduled_at', () => {
  for (const value of [
    '2026-08-01T09:00:00',
    '2026-08-01 09:00:00',
    'not-a-date',
    '2026-13-45T09:00:00Z',
    '2026-02-30T09:00:00Z',
    '2026-04-31T09:00:00Z',
    '2026-08-01T09:00:00z',
  ]) {
    assert.throws(() => {
      MailRequestBuilder.create()
        .tenantId('00000000-0000-0000-0000-000000000101')
        .sourceService('example-service')
        .mailRequestId('00000000-0000-0000-0000-000000000201')
        .purpose('FormResponseNotification')
        .to({ email: 'admin@example.com' })
        .subject('New response')
        .textBody('A new response arrived.')
        .scheduledAt(value)
        .build();
    }, MailRequestValidationError);
  }
});

test('MailRequestBuilder allows explicit null scheduled_at', () => {
  const request = MailRequestBuilder.create()
    .tenantId('00000000-0000-0000-0000-000000000101')
    .sourceService('example-service')
    .mailRequestId('00000000-0000-0000-0000-000000000201')
    .purpose('FormResponseNotification')
    .to({ email: 'admin@example.com' })
    .subject('New response')
    .textBody('A new response arrived.')
    .scheduledAt(null)
    .build();

  assert.equal(Object.hasOwn(request, 'scheduled_at'), true);
  assert.equal(request.scheduled_at, null);
});

test('scheduled_at does not affect payload_hash', () => {
  const base = buildSampleRequest();
  const scheduled = MailRequestBuilder.create()
    .tenantId('00000000-0000-0000-0000-000000000101')
    .sourceService('example-service')
    .mailRequestId('00000000-0000-0000-0000-000000000201')
    .purpose('FormResponseNotification')
    .to({ email: 'admin@example.com' })
    .subject('New response')
    .textBody('A new response arrived.')
    .scheduledAt('2026-08-01T09:00:00Z')
    .build();

  assert.equal(base.payload_hash, scheduled.payload_hash);
  assert.notEqual(base.scheduled_at, scheduled.scheduled_at);
});

test('MailRequestBuilder rejects invalid source_service', () => {
  assert.throws(() => {
    MailRequestBuilder.create()
      .tenantId('00000000-0000-0000-0000-000000000101')
      .sourceService('INVALID')
      .mailRequestId('00000000-0000-0000-0000-000000000201')
      .purpose('FormResponseNotification')
      .to({ email: 'one@example.com' })
      .subject('x')
      .textBody('body')
      .build();
  }, MailRequestValidationError);
});

test('generateUuidV7 returns version 7 UUID', () => {
  const id = generateUuidV7(Date.parse('2026-07-21T12:00:00.000Z'));
  assert.match(id, /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
});

async function withMockServer(handler, run) {
  const server = createServer(handler);
  await new Promise((resolve) => {
    server.listen(0, '127.0.0.1', resolve);
  });

  const address = server.address();
  const port = typeof address === 'object' && address ? address.port : 0;
  const baseUrl = `http://127.0.0.1:${port}`;

  try {
    return await run(baseUrl);
  } finally {
    await new Promise((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
    });
  }
}

test('MailerClient posts builder scheduled_at field', async () => {
  /** @type {Record<string, unknown> | null} */
  let captured = null;

  await withMockServer((req, res) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      captured = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      res.writeHead(202, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({
        mail_request_id: '00000000-0000-0000-0000-000000000201',
        status: 'accepted',
      }));
    });
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    const request = MailRequestBuilder.create()
      .tenantId('00000000-0000-0000-0000-000000000101')
      .sourceService('example-service')
      .mailRequestId('00000000-0000-0000-0000-000000000201')
      .purpose('FormResponseNotification')
      .to({ email: 'admin@example.com' })
      .subject('New response')
      .textBody('A new response arrived.')
      .scheduledAt('2026-08-01T09:00:00Z')
      .build();

    const response = await client.sendMail(request);
    assert.equal(response.status, MailRequestAcceptanceStatus.Accepted);
    assert.equal(captured?.scheduled_at, '2026-08-01T09:00:00Z');
    assert.equal(captured?.payload_hash, request.payload_hash);
  });
});

test('MailerClient handles accepted and already_accepted', async () => {
  let callCount = 0;

  await withMockServer((_req, res) => {
    callCount += 1;
    const status = callCount === 1 ? 'accepted' : 'already_accepted';
    res.writeHead(202, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      mail_request_id: '00000000-0000-0000-0000-000000000201',
      status,
    }));
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    const request = buildSampleRequest();

    const first = await client.sendMail(request);
    assert.equal(first.status, MailRequestAcceptanceStatus.Accepted);
    assert.equal(first.isFirstAcceptance, true);

    const second = await client.sendMail(request);
    assert.equal(second.status, MailRequestAcceptanceStatus.AlreadyAccepted);
    assert.equal(second.isIdempotentResend, true);
  });
});

test('MailerClient maps 409 IDEMPOTENCY_CONFLICT', async () => {
  await withMockServer((_req, res) => {
    res.writeHead(409, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ code: 'IDEMPOTENCY_CONFLICT' }));
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    await assert.rejects(
      () => client.sendMail(buildSampleRequest()),
      MailerIdempotencyConflictError,
    );
  });
});

test('MailerClient maps 422 validation errors', async () => {
  await withMockServer((_req, res) => {
    res.writeHead(422, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ code: 'INVALID_PAYLOAD_HASH' }));
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    await assert.rejects(
      () => client.sendMail(buildSampleRequest()),
      MailerValidationError,
    );
  });
});

test('MailerClient retries retryable 503 then succeeds', async () => {
  let attempts = 0;

  await withMockServer((_req, res) => {
    attempts += 1;
    if (attempts === 1) {
      res.writeHead(503, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'MAILER_TEMPORARILY_UNAVAILABLE', retryable: true }));
      return;
    }

    res.writeHead(202, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
      mail_request_id: '00000000-0000-0000-0000-000000000201',
      status: 'accepted',
    }));
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    const response = await client.sendMail(buildSampleRequest(), {
      maxRetries: 2,
      baseDelayMs: 1,
    });

    assert.equal(response.status, MailRequestAcceptanceStatus.Accepted);
    assert.equal(attempts, 2);
  });
});

test('MailerClient wraps transport failures as MailerRetryableError', async () => {
  const client = new MailerClient({
    baseUrl: 'http://127.0.0.1:1',
    bearerToken: 'token',
    timeoutMs: 500,
  });

  await assert.rejects(
    () => client.sendMail(buildSampleRequest(), { maxRetries: 0 }),
    (error) => error instanceof MailerRetryableError,
  );
});

test('MailerClient retries transport failures before giving up', async () => {
  const start = Date.now();
  const client = new MailerClient({
    baseUrl: 'http://127.0.0.1:1',
    bearerToken: 'token',
    timeoutMs: 500,
  });

  await assert.rejects(
    () => client.sendMail(buildSampleRequest(), { maxRetries: 2, baseDelayMs: 50 }),
    MailerRetryableError,
  );

  assert.ok(Date.now() - start >= 100, 'expected backoff delays between transport retries');
});

test('MailerClient surfaces retryable errors after max retries', async () => {
  await withMockServer((_req, res) => {
    res.writeHead(503, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ code: 'MAILER_TEMPORARILY_UNAVAILABLE', retryable: true }));
  }, async (baseUrl) => {
    const client = new MailerClient({ baseUrl, bearerToken: 'token' });
    await assert.rejects(
      () => client.sendMail(buildSampleRequest(), { maxRetries: 1, baseDelayMs: 1 }),
      MailerRetryableError,
    );
  });
});
