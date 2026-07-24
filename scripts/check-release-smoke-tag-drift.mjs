#!/usr/bin/env node

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const errors = [];

// Update this constant when the latest published release tag changes.
const expectedImageTag = 'v0.9.2';
const expectedSupportedVersion = expectedImageTag.slice(1);

function read(relativePath) {
  return readFileSync(path.join(root, relativePath), 'utf8');
}

function fail(message) {
  errors.push(message);
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

const releaseRecordPath = `docs/releases/${expectedImageTag}.md`;
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
  `Release smoke default tag drift check passed: README, release smoke docs/scripts/compose, `
  + `and SECURITY supported version are aligned on ${expectedImageTag}.`,
);
