#!/usr/bin/env node
import { MailerClient, MailRequestBuilder } from '../src/index.mjs';

const client = new MailerClient({
  baseUrl: process.env.MAILER_BASE_URL ?? 'http://127.0.0.1:5280',
  bearerToken: process.env.MAIL_SERVICE_TOKEN ?? 'local-mail-service-token',
});

const response = await client.sendMail(
  MailRequestBuilder.create()
    .tenantId(process.env.MAILER_TENANT_ID ?? '00000000-0000-0000-0000-000000000101')
    .sourceService(process.env.MAILER_SOURCE_SERVICE ?? 'example-service')
    .generateMailRequestId()
    .purpose('FormResponseNotification')
    .to({ email: process.env.MAILER_RECIPIENT_EMAIL ?? 'admin@example.com' })
    .subject('SDK smoke test')
    .textBody('Sent from @amane/mailer SDK.')
    .build(),
);

console.log(`HTTP 202 - status: ${response.status}`);
console.log(`mail_request_id: ${response.mailRequestId}`);
