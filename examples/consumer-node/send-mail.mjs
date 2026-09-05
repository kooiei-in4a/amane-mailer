#!/usr/bin/env node
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { randomUUID } from 'node:crypto';

const DEFAULT_MAILER_BASE_URL = 'http://127.0.0.1:5280/';
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
    mailServiceToken: process.env.MAILER_API_KEY,
    recipientEmail: process.env.MAILER_RECIPIENT_EMAIL ?? DEFAULT_RECIPIENT_EMAIL,
    requestId: readOptionValue(args, '--request-id') ?? randomUUID(),
    mutate: args.includes('--mutate'),
    timeoutSeconds,
  };
}

function buildEndpoint(mailerBaseUrl) {
  return new URL('api/mail-requests', mailerBaseUrl.endsWith('/')
    ? mailerBaseUrl
    : `${mailerBaseUrl}/`);
}

function buildMailRequest(options) {
  const mailRequest = {
    mail_request_id: options.requestId,
    purpose: 'FormResponseNotification',
    to: [{ email: options.recipientEmail }],
    subject: options.mutate ? 'New response (edited)' : 'New response',
    text_body: 'A new response arrived.',
  };
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
      console.log('This mail_request_id was already accepted with the same canonical payload;');
      console.log('the Mailer treated this POST as an idempotent resend.');
    }
    return;
  }

  if (statusCode === 409) {
    console.log(`HTTP 409 Conflict: ${responseBody}`);
    console.log();
    console.log(`mail_request_id ${requestId} was already accepted with a different`);
    console.log('payload. Reusing a mail_request_id after changing subject, body,');
    console.log('recipients, or metadata returns IDEMPOTENCY_CONFLICT.');
    return;
  }

  console.log(`HTTP ${statusCode} ${statusMessage}: ${responseBody}`);
}

async function main() {
  const options = parseOptions(process.argv.slice(2));
  if (!options.mailServiceToken) {
    throw new Error('MAILER_API_KEY must contain a managed API key.');
  }
  const mailRequest = buildMailRequest(options);
  const endpoint = buildEndpoint(options.mailerBaseUrl);

  console.log(`POST ${endpoint.href}`);
  console.log(`mail_request_id: ${options.requestId}`);
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
