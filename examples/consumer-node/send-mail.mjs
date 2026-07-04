#!/usr/bin/env node
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { randomUUID } from 'node:crypto';

import { computeDeliveryPayloadSha256Hex } from '../payload-hash/javascript/mail_payload_hash.mjs';

const DEFAULT_MAILER_BASE_URL = 'http://127.0.0.1:5280/';
const DEFAULT_MAIL_SERVICE_TOKEN = 'local-mail-service-token';
const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000101';
const DEFAULT_SOURCE_SERVICE = 'example-service';
const DEFAULT_RECIPIENT_EMAIL = 'admin@example.com';
const DEFAULT_TIMEOUT_SECONDS = 10;

function readOptionValue(args, name) {
  const index = args.indexOf(name);
  if (index === -1) {
    return undefined;
  }
  if (index === args.length - 1 || args[index + 1].startsWith('--')) {
    throw new Error(`${name} requires a value.`);
  }
  return args[index + 1];
}

function parseOptions(args) {
  const timeoutSecondsText = readOptionValue(args, '--timeout-seconds')
    ?? process.env.MAILER_TIMEOUT_SECONDS
    ?? DEFAULT_TIMEOUT_SECONDS.toString();
  const timeoutSeconds = Number(timeoutSecondsText);

  if (!Number.isFinite(timeoutSeconds) || timeoutSeconds <= 0) {
    throw new Error('--timeout-seconds must be a positive number.');
  }

  return {
    mailerBaseUrl: process.env.MAILER_BASE_URL ?? DEFAULT_MAILER_BASE_URL,
    mailServiceToken: process.env.MAIL_SERVICE_TOKEN ?? DEFAULT_MAIL_SERVICE_TOKEN,
    tenantId: process.env.MAILER_TENANT_ID ?? DEFAULT_TENANT_ID,
    sourceService: process.env.MAILER_SOURCE_SERVICE ?? DEFAULT_SOURCE_SERVICE,
    recipientEmail: process.env.MAILER_RECIPIENT_EMAIL ?? DEFAULT_RECIPIENT_EMAIL,
    requestId: readOptionValue(args, '--request-id') ?? randomUUID(),
    mutate: args.includes('--mutate'),
    timeoutSeconds,
  };
}

function buildEndpoint(mailerBaseUrl) {
  return new URL('internal/mail-requests', mailerBaseUrl.endsWith('/')
    ? mailerBaseUrl
    : `${mailerBaseUrl}/`);
}

function buildMailRequest(options) {
  const mailRequest = {
    tenant_id: options.tenantId,
    mail_request_id: options.requestId,
    source_service: options.sourceService,
    purpose: 'FormResponseNotification',
    to: [{ email: options.recipientEmail }],
    subject: options.mutate ? 'New response (edited)' : 'New response',
    text_body: 'A new response arrived.',
    payload_hash: '',
  };

  mailRequest.payload_hash = computeDeliveryPayloadSha256Hex(mailRequest);
  return mailRequest;
}

function postJson(endpoint, token, body, timeoutSeconds) {
  const bodyBytes = Buffer.from(JSON.stringify(body), 'utf8');
  const requestModule = endpoint.protocol === 'https:' ? httpsRequest : httpRequest;

  return new Promise((resolve, reject) => {
    const request = requestModule(
      endpoint,
      {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          Accept: 'application/json',
          'Content-Length': bodyBytes.length,
        },
        timeout: timeoutSeconds * 1000,
      },
      (response) => {
        response.setEncoding('utf8');

        let responseBody = '';
        response.on('data', (chunk) => {
          responseBody += chunk;
        });
        response.on('end', () => {
          resolve({
            statusCode: response.statusCode ?? 0,
            statusMessage: response.statusMessage ?? '',
            body: responseBody,
          });
        });
      },
    );

    request.on('timeout', () => {
      request.destroy(new Error(`POST timed out after ${timeoutSeconds} seconds.`));
    });
    request.on('error', reject);
    request.end(bodyBytes);
  });
}

function printResult(statusCode, statusMessage, responseBody, requestId) {
  if (statusCode === 202) {
    const response = JSON.parse(responseBody);
    console.log(`HTTP 202 Accepted - status: ${response.status}`);

    if (response.status === 'accepted') {
      console.log('The Mailer accepted this request for asynchronous delivery.');
    } else if (response.status === 'already_accepted') {
      console.log('This mail_request_id was already accepted with the same payload_hash;');
      console.log('the Mailer treated this POST as an idempotent resend.');
    }
    return;
  }

  if (statusCode === 409) {
    console.log(`HTTP 409 Conflict: ${responseBody}`);
    console.log();
    console.log(`mail_request_id ${requestId} was already accepted with a different`);
    console.log('payload_hash. Reusing a mail_request_id after changing subject, body,');
    console.log('recipients, or metadata returns IDEMPOTENCY_CONFLICT.');
    return;
  }

  console.log(`HTTP ${statusCode} ${statusMessage}: ${responseBody}`);
}

async function main() {
  const options = parseOptions(process.argv.slice(2));
  const mailRequest = buildMailRequest(options);
  const endpoint = buildEndpoint(options.mailerBaseUrl);

  console.log(`POST ${endpoint.href}`);
  console.log(`mail_request_id: ${options.requestId}`);
  console.log(`payload_hash:    ${mailRequest.payload_hash}`);
  console.log();

  const response = await postJson(
    endpoint,
    options.mailServiceToken,
    mailRequest,
    options.timeoutSeconds,
  );
  printResult(response.statusCode, response.statusMessage, response.body, options.requestId);

  return response.statusCode === 202 || response.statusCode === 409 ? 0 : 1;
}

try {
  process.exitCode = await main();
} catch (error) {
  if (error instanceof SyntaxError) {
    console.error(`[error] Invalid JSON response: ${error.message}`);
  } else {
    console.error(`[error] ${error.message}`);
  }
  process.exitCode = 2;
}
