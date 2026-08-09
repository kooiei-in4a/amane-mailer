import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  buildDeliveryPayloadJson,
  computeDeliveryPayloadSha256Hex,
} from '../src/payload-hash.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const vectorsPaths = [
  path.join(root, 'tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json'),
  path.join(
    root,
    'tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-recipient-v1.3-vectors.json',
  ),
];
const vectors = vectorsPaths.flatMap((vectorsPath) => JSON.parse(readFileSync(vectorsPath, 'utf8')));

test('payload_hash matches official test vectors', () => {
  for (const vector of vectors) {
    const {
      name,
      input,
      attachments = null,
      expected_canonical_json: expectedCanonical,
      expected_sha256_hex: expectedHash,
    } = vector;

    assert.equal(
      buildDeliveryPayloadJson(input, attachments),
      expectedCanonical,
      `${name}: canonical JSON mismatch`,
    );
    assert.equal(
      computeDeliveryPayloadSha256Hex(input, attachments),
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
      buildDeliveryPayloadJson(envelopeRequest, attachments),
      expectedCanonical,
      `${name}: delivery payload JSON mismatch`,
    );
    assert.equal(
      computeDeliveryPayloadSha256Hex(envelopeRequest, attachments),
      expectedHash,
      `${name}: delivery payload hash mismatch`,
    );
  }
});
