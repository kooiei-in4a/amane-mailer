#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  buildDeliveryPayloadJson,
  canonicalize,
  computeDeliveryPayloadSha256Hex,
  computeSha256Hex,
} from './mail_payload_hash.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const vectorsPath = path.join(
  root,
  'tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json',
);
const vectors = JSON.parse(readFileSync(vectorsPath, 'utf8'));

for (const vector of vectors) {
  const { name, input, expected_canonical_json: expectedCanonical, expected_sha256_hex: expectedHash } = vector;

  const actualCanonical = canonicalize(input);
  if (actualCanonical !== expectedCanonical) {
    console.error(`[FAIL] ${name}: canonical JSON mismatch`);
    console.error(`  expected: ${expectedCanonical}`);
    console.error(`  actual:   ${actualCanonical}`);
    process.exit(1);
  }

  const actualHash = computeSha256Hex(input);
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
  const deliveryJson = buildDeliveryPayloadJson(envelopeRequest);
  if (deliveryJson !== expectedCanonical) {
    console.error(`[FAIL] ${name}: delivery payload JSON mismatch`);
    process.exit(1);
  }

  const deliveryHash = computeDeliveryPayloadSha256Hex(envelopeRequest);
  if (deliveryHash !== expectedHash) {
    console.error(`[FAIL] ${name}: delivery payload hash mismatch`);
    process.exit(1);
  }
}

console.log(`JavaScript payload_hash examples passed (${vectors.length} vectors).`);
