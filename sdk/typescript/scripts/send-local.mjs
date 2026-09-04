#!/usr/bin/env node
import { MailerClient, MailRequestBuilder } from '../src/index.mjs';

if (!process.env.MAILER_API_KEY) {
  throw new Error('MAILER_API_KEY must contain a managed API key.');
}

const client = new MailerClient({
  baseUrl: process.env.MAILER_BASE_URL ?? 'http://127.0.0.1:5280',
  bearerToken: process.env.MAILER_API_KEY,
});

const response = await client.sendMail(
  MailRequestBuilder.create()
    .generateMailRequestId()
    .purpose('FormResponseNotification')
    .to({ email: process.env.MAILER_RECIPIENT_EMAIL ?? 'admin@example.com' })
    .subject('SDK smoke test')
    .textBody('Sent from @amane/mailer SDK.')
    .build(),
);

console.log(`HTTP 202 - status: ${response.status}`);
console.log(`mail_request_id: ${response.mailRequestId}`);
