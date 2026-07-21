import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';

import { MailRequestBuilder } from './builder.mjs';
import {
  MailRequestAcceptedResponse,
  MailRequestAcceptanceStatus,
  parseMailerError,
} from './errors.mjs';

function sleep(ms) {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

function buildEndpoint(baseUrl) {
  const normalized = baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`;
  return new URL('internal/mail-requests', normalized);
}

function serializeMailRequest(request) {
  return JSON.stringify(request);
}

function postJson(endpoint, token, body, timeoutMs) {
  const bodyBytes = Buffer.from(body, 'utf8');
  const requestModule = endpoint.protocol === 'https:' ? httpsRequest : httpRequest;

  return new Promise((resolve, reject) => {
    const req = requestModule(
      endpoint,
      {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          Accept: 'application/json',
          'Content-Length': bodyBytes.length,
        },
        timeout: timeoutMs,
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
            body: responseBody,
          });
        });
      },
    );

    req.on('timeout', () => {
      req.destroy(new Error(`POST timed out after ${timeoutMs} ms.`));
    });
    req.on('error', reject);
    req.end(bodyBytes);
  });
}

export class MailerClient {
  constructor({ baseUrl, bearerToken, timeoutMs = 10_000 }) {
    if (!baseUrl || !bearerToken) {
      throw new Error('baseUrl and bearerToken are required.');
    }

    this.baseUrl = baseUrl;
    this.bearerToken = bearerToken;
    this.timeoutMs = timeoutMs;
  }

  async sendMail(mailRequest, options = {}) {
    const maxRetries = options.maxRetries ?? 3;
    const baseDelayMs = options.baseDelayMs ?? 200;

    for (let attempt = 0; attempt <= maxRetries; attempt += 1) {
      try {
        return await this.#postOnce(mailRequest);
      } catch (error) {
        if (error.retryable === true && attempt < maxRetries) {
          await sleep(baseDelayMs * (2 ** attempt));
          continue;
        }
        throw error;
      }
    }

    throw new Error('sendMail exhausted retries without a response.');
  }

  async #postOnce(mailRequest) {
    const endpoint = buildEndpoint(this.baseUrl);
    const response = await postJson(
      endpoint,
      this.bearerToken,
      serializeMailRequest(mailRequest),
      this.timeoutMs,
    );

    if (response.statusCode === 202) {
      const body = JSON.parse(response.body);
      const status = body.status;
      if (
        status !== MailRequestAcceptanceStatus.Accepted
        && status !== MailRequestAcceptanceStatus.AlreadyAccepted
      ) {
        throw parseMailerError(response.statusCode, response.body);
      }

      return new MailRequestAcceptedResponse({
        mailRequestId: body.mail_request_id,
        status,
      });
    }

    throw parseMailerError(response.statusCode, response.body);
  }
}

export { MailRequestBuilder };
