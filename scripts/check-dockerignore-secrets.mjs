#!/usr/bin/env node

import { readFileSync } from 'node:fs';
import { basename, relative } from 'node:path';

const dockerignorePath = process.argv[2] ?? '.dockerignore';
const source = readFileSync(dockerignorePath, 'utf8');
const patterns = source
  .split(/\r?\n/)
  .map((line) => line.trim())
  .filter((line) => line.length > 0 && !line.startsWith('#'));

const requiredPatterns = [
  'config/mailer/tenants.local*.json',
  'config/mailer/tenants.production*.json',
  'infra/deploy/keys/',
  'infra/deploy/rclone/',
  'infra/deploy/restore/',
  'infra/deploy/secrets/',
  'infra/deploy/config/platform-sender/',
  '*.db.age',
  '*.env',
  'infra/deploy/data/',
  'infra/deploy/tenants.json',
  '*.db',
];

const requiredAllows = [
  'config/mailer/tenants.example.json',
  'config/mailer/tenants.local-acs.json.example',
  'config/mailer/tenants.schema.json',
];

const sentinelFiles = [
  'config/mailer/tenants.local.json',
  'config/mailer/tenants.production.json',
  'infra/deploy/keys/id_rsa',
  'infra/deploy/rclone/rclone.conf',
  'infra/deploy/restore/latest.db',
  'infra/deploy/data/mailer.db',
  'infra/deploy/tenants.json',
  'infra/deploy/secrets/acs/acs_connection_string',
  'infra/deploy/config/platform-sender/platform-sender.json',
  'infra/deploy/secrets.env',
  'tenant.env',
  'backup/mailer.db.age',
];

const errors = [];

for (const pattern of requiredPatterns) {
  if (!patterns.includes(pattern)) {
    errors.push(`Missing required .dockerignore pattern: ${pattern}`);
  }
}

for (const pattern of requiredAllows) {
  if (!patterns.includes(`!${pattern}`)) {
    errors.push(`Missing required .dockerignore allow rule: !${pattern}`);
  }
}

function patternToRegExp(pattern) {
  const directoryPrefix = pattern.endsWith('/');
  const normalized = pattern.replace(/^\//, '').replace(/\/$/, '');
  const escaped = normalized
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*\*/g, '§§')
    .replace(/\*/g, '[^/]*')
    .replace(/§§/g, '.*')
    .replace(/\?/g, '[^/]');
  const suffix = directoryPrefix ? '(?:/.*)?' : '';
  return new RegExp(`^${escaped}${suffix}$`);
}

function isIgnored(relativePath, patternList) {
  let ignored = false;

  for (const rawPattern of patternList) {
    const negated = rawPattern.startsWith('!');
    const pattern = negated ? rawPattern.slice(1) : rawPattern;
    const regex = patternToRegExp(pattern);
    const fileName = basename(relativePath);
    const matches =
      regex.test(relativePath.replaceAll('\\', '/')) ||
      regex.test(fileName);

    if (matches) {
      ignored = !negated;
    }
  }

  return ignored;
}

for (const sentinel of sentinelFiles) {
  if (!isIgnored(sentinel, patterns)) {
    errors.push(`Sentinel file would be included in Docker build context: ${sentinel}`);
  }
}

for (const allowed of requiredAllows) {
  if (isIgnored(allowed, patterns)) {
    errors.push(`Safe example file would be excluded from Docker build context: ${allowed}`);
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(error);
  }

  process.exit(1);
}

console.log(
  `OK: ${dockerignorePath} excludes private deploy material and keeps safe tenant examples.`,
);
