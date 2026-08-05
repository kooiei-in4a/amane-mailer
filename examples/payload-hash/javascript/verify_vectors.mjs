#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  buildDeliveryPayloadJson,
  computeDeliveryPayloadSha256Hex,
} from './mail_payload_hash.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const vectorsDir = path.join(root, 'tests/Amane.Mailer.Contracts.Tests/TestVectors');
// Baseline: pre-ADR-0023 single-To/attachment fixture, also read by the Python/TypeScript SDK
// test suites (sdk/python, sdk/typescript), which do not yet implement cc/bcc (issue #542).
// Recipient v1.3: ADR 0023 to/cc/bcc conformance vectors, verified here and by the .NET
// Contracts layer, but intentionally NOT read by the SDK test suites until #542 lands.
const vectorFiles = [
  path.join(vectorsDir, 'payload-hash-vectors.json'),
  path.join(vectorsDir, 'payload-hash-recipient-v1.3-vectors.json'),
];
const vectors = vectorFiles.flatMap((vectorsPath) => JSON.parse(readFileSync(vectorsPath, 'utf8')));

for (const vector of vectors) {
  const {
    name,
    input,
    attachments = null,
    expected_canonical_json: expectedCanonical,
    expected_sha256_hex: expectedHash,
  } = vector;

  const actualCanonical = buildDeliveryPayloadJson(input, attachments);
  if (actualCanonical !== expectedCanonical) {
    console.error(`[FAIL] ${name}: canonical JSON mismatch`);
    console.error(`  expected: ${expectedCanonical}`);
    console.error(`  actual:   ${actualCanonical}`);
    process.exit(1);
  }

  const actualHash = computeDeliveryPayloadSha256Hex(input, attachments);
  if (actualHash !== expectedHash) {
    console.error(`[FAIL] ${name}: SHA-256 mismatch`);
    console.error(`  expected: ${expectedHash}`);
    console.error(`  actual:   ${actualHash}`);
    process.exit(1);
  }

  const envelopeRequest = {
    tenant_id: '00000000-0000-0000-0000-000000000101',
    mail_request_id: '00000000-0000-0000-0000-000000000201',
    payload_hash: 'caller-provided-placeholder',
    ...input,
  };
  const deliveryJson = buildDeliveryPayloadJson(envelopeRequest, attachments);
  if (deliveryJson !== expectedCanonical) {
    console.error(`[FAIL] ${name}: delivery payload JSON mismatch`);
    process.exit(1);
  }

  const deliveryHash = computeDeliveryPayloadSha256Hex(envelopeRequest, attachments);
  if (deliveryHash !== expectedHash) {
    console.error(`[FAIL] ${name}: delivery payload hash mismatch`);
    process.exit(1);
  }
}

console.log(`JavaScript payload_hash examples passed (${vectors.length} vectors).`);
