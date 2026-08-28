#!/usr/bin/env node

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const errors = [];

const authorityPath = 'release/current-public.json';

function fail(message) {
  errors.push(message);
}

function read(relativePath) {
  return readFileSync(path.join(root, relativePath), 'utf8');
}

function parseCurrentPublicAuthority() {
  const absolutePath = path.join(root, authorityPath);
  if (!existsSync(absolutePath)) {
    fail(`Missing current-public authority: ${authorityPath}.`);
    return null;
  }

  let parsed;
  try {
    parsed = JSON.parse(read(authorityPath));
  } catch {
    fail(`Malformed JSON in ${authorityPath}.`);
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    fail(`${authorityPath} must be a JSON object.`);
    return null;
  }

  if (parsed.schemaVersion !== 1) {
    fail(`${authorityPath} schemaVersion must be 1.`);
    return null;
  }

  const versionPattern = /^[0-9]+\.[0-9]+\.[0-9]+$/;
  if (typeof parsed.version !== 'string' || !versionPattern.test(parsed.version)) {
    fail(`${authorityPath} version must be X.Y.Z.`);
    return null;
  }

  const expectedTag = `v${parsed.version}`;
  if (typeof parsed.tag !== 'string' || parsed.tag !== expectedTag) {
    fail(`${authorityPath} tag must be v<version> (${expectedTag}).`);
    return null;
  }

  if (!Array.isArray(parsed.platforms) || parsed.platforms.length === 0) {
    fail(`${authorityPath} platforms must be a non-empty array.`);
    return null;
  }

  const expectedRecord = `docs/releases/${expectedTag}.md`;
  if (typeof parsed.releaseRecord !== 'string' || parsed.releaseRecord !== expectedRecord) {
    fail(`${authorityPath} releaseRecord must be ${expectedRecord}.`);
    return null;
  }

  if (!existsSync(path.join(root, parsed.releaseRecord))) {
    fail(`${authorityPath} releaseRecord file is missing: ${parsed.releaseRecord}.`);
    return null;
  }

  return {
    version: parsed.version,
    tag: parsed.tag,
    releaseRecord: parsed.releaseRecord,
  };
}

function assertEqual(label, actual, expected) {
  if (actual !== expected) {
    fail(`${label} expected '${expected}' but found '${actual ?? '(missing)'}'.`);
  }
}

function firstMatch(source, pattern, label) {
  const match = source.match(pattern);
  if (!match) {
    fail(`${label} is missing a match for ${pattern}.`);
    return undefined;
  }

  return match[1];
}

function assertContains(source, needle, label) {
  if (!source.includes(needle)) {
    fail(`${label} is missing '${needle}'.`);
  }
}

const authority = parseCurrentPublicAuthority();
if (!authority) {
  console.error('Release smoke default tag drift check failed:');
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

const expectedImageTag = authority.tag;
const expectedSupportedVersion = authority.version;
const releaseRecordPath = authority.releaseRecord;

const releaseSmokeSh = read('scripts/release-smoke.sh');
const releaseSmokePs1 = read('scripts/release-smoke.ps1');
const releaseSmokeCompose = read('infra/docker/docker-compose.release-smoke.yml');
const releaseSmokeDocJa = read('docs/ops/release-image-smoke.md');
const releaseSmokeDocEn = read('docs/ops/release-image-smoke.en.md');
const readmeJa = read('README.md');
const readmeEn = read('README.en.md');
const security = read('SECURITY.md');

assertEqual(
  'scripts/release-smoke.sh default MAILER_IMAGE_TAG',
  firstMatch(releaseSmokeSh, /MAILER_IMAGE_TAG:-(v[\d.]+)/, 'scripts/release-smoke.sh'),
  expectedImageTag,
);

assertEqual(
  'scripts/release-smoke.ps1 default MAILER_IMAGE_TAG',
  firstMatch(releaseSmokePs1, /Get-EnvOrDefault 'MAILER_IMAGE_TAG' '(v[\d.]+)'/, 'scripts/release-smoke.ps1'),
  expectedImageTag,
);

const composeDefaults = [
  ...releaseSmokeCompose.matchAll(/MAILER_IMAGE_TAG:-(v[\d.]+)/g),
].map((match) => match[1]);

if (composeDefaults.length === 0) {
  fail('infra/docker/docker-compose.release-smoke.yml is missing MAILER_IMAGE_TAG defaults.');
} else {
  for (const [index, tag] of composeDefaults.entries()) {
    assertEqual(
      `infra/docker/docker-compose.release-smoke.yml default MAILER_IMAGE_TAG #${index + 1}`,
      tag,
      expectedImageTag,
    );
  }
}

for (const [label, source] of [
  ['docs/ops/release-image-smoke.md MAILER_IMAGE_TAG table', releaseSmokeDocJa],
  ['docs/ops/release-image-smoke.en.md MAILER_IMAGE_TAG table', releaseSmokeDocEn],
]) {
  assertEqual(
    label,
    firstMatch(source, /\| `MAILER_IMAGE_TAG` \| `(v[\d.]+)` \|/, label),
    expectedImageTag,
  );
}

for (const [label, source] of [
  ['README.md published image default tag', readmeJa],
  ['README.en.md published image default tag', readmeEn],
]) {
  assertEqual(
    label,
    firstMatch(source, /ghcr\.io\/kooiei-in4a\/amane-mailer:(v[\d.]+)/, label),
    expectedImageTag,
  );
}

for (const [label, source] of [
  ['docs/ops/release-image-smoke.md intro image tag', releaseSmokeDocJa],
  ['docs/ops/release-image-smoke.en.md intro image tag', releaseSmokeDocEn],
]) {
  assertEqual(
    label,
    firstMatch(source, /ghcr\.io\/kooiei-in4a\/amane-mailer:(v[\d.]+)/, label),
    expectedImageTag,
  );
}

assertContains(
  security,
  `| ${expectedSupportedVersion}   | Yes (latest release) |`,
  'SECURITY.md supported version table',
);

if (!existsSync(path.join(root, releaseRecordPath))) {
  fail(`Missing release record for ${expectedImageTag}: ${releaseRecordPath}.`);
} else {
  for (const [label, source] of [
    ['docs/ops/release-image-smoke.md recorded smoke results link', releaseSmokeDocJa],
    ['docs/ops/release-image-smoke.en.md recorded smoke results link', releaseSmokeDocEn],
  ]) {
    assertContains(source, releaseRecordPath, label);
  }
}

if (errors.length > 0) {
  console.error('Release smoke default tag drift check failed:');
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(
  `Release smoke default tag drift check passed: authority ${expectedImageTag}, README, release smoke docs/scripts/compose, `
  + `and SECURITY supported version are aligned.`,
);
