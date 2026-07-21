import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  buildDeliveryPayloadJson,
  canonicalize,
  computeDeliveryPayloadSha256Hex,
  computeSha256Hex,
} from '../src/payload-hash.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const vectorsPath = path.join(
  root,
  'tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json',
);
const vectors = JSON.parse(readFileSync(vectorsPath, 'utf8'));

test('payload_hash matches official test vectors', () => {
  for (const vector of vectors) {
    const { name, input, expected_canonical_json: expectedCanonical, expected_sha256_hex: expectedHash } = vector;

    assert.equal(
      canonicalize(input),
      expectedCanonical,
      `${name}: canonical JSON mismatch`,
    );
    assert.equal(
      computeSha256Hex(input),
      expectedHash,
      `${name}: SHA-256 mismatch`,
    );

    const envelopeRequest = {
      tenant_id: '00000000-0000-0000-0000-000000000101',
      mail_request_id: '00000000-0000-0000-0000-000000000201',
      payload_hash: 'caller-provided-placeholder',
      ...input,
    };

    assert.equal(
      buildDeliveryPayloadJson(envelopeRequest),
      expectedCanonical,
      `${name}: delivery payload JSON mismatch`,
    );
    assert.equal(
      computeDeliveryPayloadSha256Hex(envelopeRequest),
      expectedHash,
      `${name}: delivery payload hash mismatch`,
    );
  }
});
