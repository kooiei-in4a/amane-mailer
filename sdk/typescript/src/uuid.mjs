import { randomBytes, randomUUID } from 'node:crypto';

function formatUuid(bytes) {
  const hex = Buffer.from(bytes).toString('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Generates a UUIDv7 (RFC 9562). Falls back to UUIDv4 when timestamp exceeds 48-bit range.
 */
export function generateUuidV7(now = Date.now()) {
  if (!Number.isFinite(now) || now < 0 || now >= 0x1000000000000) {
    return randomUUID();
  }

  const bytes = randomBytes(10);
  const timestamp = BigInt(Math.trunc(now));

  const uuidBytes = new Uint8Array(16);
  uuidBytes[0] = Number((timestamp >> 40n) & 0xffn);
  uuidBytes[1] = Number((timestamp >> 32n) & 0xffn);
  uuidBytes[2] = Number((timestamp >> 24n) & 0xffn);
  uuidBytes[3] = Number((timestamp >> 16n) & 0xffn);
  uuidBytes[4] = Number((timestamp >> 8n) & 0xffn);
  uuidBytes[5] = Number(timestamp & 0xffn);
  uuidBytes[6] = (bytes[0] & 0x0f) | 0x70;
  uuidBytes[7] = bytes[1];
  uuidBytes[8] = (bytes[2] & 0x3f) | 0x80;
  uuidBytes[9] = bytes[3];
  uuidBytes[10] = bytes[4];
  uuidBytes[11] = bytes[5];
  uuidBytes[12] = bytes[6];
  uuidBytes[13] = bytes[7];
  uuidBytes[14] = bytes[8];
  uuidBytes[15] = bytes[9];

  return formatUuid(uuidBytes);
}

/**
 * Preferred mail_request_id generator. Uses UUIDv7; documents UUIDv4 fallback for edge cases.
 */
export function generateMailRequestId() {
  return generateUuidV7();
}
