#!/usr/bin/env node

import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const root = process.cwd();
const errors = [];

const authorityPath = 'release/current-public.json';

const governedReleaseSmokeCommandExamplePaths = [
  'README.md',
  'README.en.md',
  'docs/ops/release-image-smoke.md',
  'docs/ops/release-image-smoke.en.md',
];

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

function assertNotContains(source, needle, label) {
  if (source.includes(needle)) {
    fail(`${label} must not contain '${needle}'.`);
  }
}

function assertNoMatch(source, pattern, label) {
  if (pattern.test(source)) {
    fail(`${label} must not match ${pattern}.`);
  }
}

function assertMarkedVersion(source, pattern, expected, label) {
  const actual = firstMatch(source, pattern, label);
  assertEqual(label, actual, expected);
}

function assertCurrentVersionLines(source, expected, label) {
  const currentLinePattern = /(current|現行|recommended|推奨|canonical|正とする|default tag|既定タグ|published image|公開イメージ)/i;
  const historicalLinePattern = /(historical|history|過去|以前|前の|prior|then-current|導入履歴|歴史的)/i;
  const versionPattern = /\bv([0-9]+\.[0-9]+\.[0-9]+)\b/g;

  for (const [index, line] of source.split(/\r?\n/).entries()) {
    if (!currentLinePattern.test(line) || historicalLinePattern.test(line)) {
      continue;
    }

    for (const match of line.matchAll(versionPattern)) {
      assertEqual(`${label} current-version line ${index + 1}`, match[1], expected);
    }
  }
}

const releaseSmokeCommandTagPattern = /MAILER_IMAGE_TAG=(v[0-9]+\.[0-9]+\.[0-9]+)/g;

function collectReleaseSmokeCommandTags(source) {
  return [...source.matchAll(releaseSmokeCommandTagPattern)].map((match) => match[1]);
}

function assertReleaseSmokeCommandExamples(source, expectedTag, label) {
  const tags = collectReleaseSmokeCommandTags(source);
  if (tags.length === 0) {
    fail(
      `${label} is missing an explicit MAILER_IMAGE_TAG=vX.Y.Z release-smoke command example.`,
    );
    return;
  }

  for (const tag of tags) {
    assertEqual(`${label} MAILER_IMAGE_TAG example`, tag, expectedTag);
  }
}

function reportAndExit() {
  if (errors.length > 0) {
    console.error('Current public release alignment check failed:');
    for (const error of errors) {
      console.error(`- ${error}`);
    }
    process.exit(1);
  }
}

function runRepositoryCheck() {
  const authority = parseCurrentPublicAuthority();
  if (!authority) {
    reportAndExit();
    return;
  }

  const expectedImageTag = authority.tag;
  const releaseRecordPath = authority.releaseRecord;

  const releaseSmokeSh = read('scripts/release-smoke.sh');
  const releaseSmokePs1 = read('scripts/release-smoke.ps1');
  const releaseSmokeCompose = read('infra/docker/docker-compose.release-smoke.yml');
  const releaseSmokeDocJa = read('docs/ops/release-image-smoke.md');
  const releaseSmokeDocEn = read('docs/ops/release-image-smoke.en.md');
  const readmeJa = read('README.md');
  const readmeEn = read('README.en.md');
  const security = read('SECURITY.md');
  const setupGuideJa = read('docs/ops/setup-guide.md');
  const setupGuideEn = read('docs/ops/setup-guide.en.md');
  const roadmap = read('ROADMAP.md');
  const agents = read('AGENTS.md');

  let globalJson;
  try {
    globalJson = JSON.parse(read('global.json'));
  } catch {
    fail('Malformed JSON in global.json.');
  }

  if (
    typeof globalJson?.sdk?.version !== 'string' ||
    !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(globalJson.sdk.version)
  ) {
    fail('global.json sdk.version must be X.Y.Z.');
  }

  // Release-smoke execution paths must not embed implicit Mailer tag defaults (#506).
  assertNotContains(
    releaseSmokeSh,
    'MAILER_IMAGE_TAG:-',
    'scripts/release-smoke.sh implicit MAILER_IMAGE_TAG default',
  );
  assertNotContains(
    releaseSmokePs1,
    "Get-EnvOrDefault 'MAILER_IMAGE_TAG'",
    'scripts/release-smoke.ps1 implicit MAILER_IMAGE_TAG default',
  );
  assertNotContains(
    releaseSmokeCompose,
    'MAILER_IMAGE_TAG:-',
    'infra/docker/docker-compose.release-smoke.yml implicit MAILER_IMAGE_TAG default',
  );
  assertContains(
    releaseSmokeCompose,
    'MAILER_IMAGE_REFERENCE:?MAILER_IMAGE_REFERENCE is required',
    'infra/docker/docker-compose.release-smoke.yml MAILER_IMAGE_REFERENCE requirement',
  );

  // Active v2 release smoke / deploy drill Consumer contract (#730 rework).
  const mail05aNoSendSmoke = read('infra/deploy/drills/mail-05a-no-send-smoke.sh');
  const mail05aAcsDrill = read('infra/deploy/drills/mail-05a-acs-drill.sh');
  for (const [label, source] of [
    ['scripts/release-smoke.sh', releaseSmokeSh],
    ['scripts/release-smoke.ps1', releaseSmokePs1],
    ['infra/deploy/drills/mail-05a-no-send-smoke.sh', mail05aNoSendSmoke],
    ['infra/deploy/drills/mail-05a-acs-drill.sh', mail05aAcsDrill],
  ]) {
    assertNotContains(source, '/internal/mail-requests', `${label} v1 Consumer endpoint`);
    assertContains(source, '/api/mail-requests', `${label} v2 Consumer endpoint`);
    assertContains(source, 'MAILER_API_KEY', `${label} managed API key auth`);
    assertNotContains(source, 'payload_hash', `${label} caller-supplied payload_hash`);
  }

  for (const [label, source] of [
    ['scripts/release-smoke.sh', releaseSmokeSh],
    ['scripts/release-smoke.ps1', releaseSmokePs1],
  ]) {
    assertNotContains(source, 'MAIL_SERVICE_TOKEN', `${label} v1 MAIL_SERVICE_TOKEN`);
    assertNotContains(source, 'TENANT_ID', `${label} Consumer TENANT_ID`);
    assertNotContains(source, 'SOURCE_SERVICE', `${label} Consumer SOURCE_SERVICE`);
  }

  for (const [label, source] of [
    ['docs/ops/release-image-smoke.md MAILER_IMAGE_TAG required note', releaseSmokeDocJa],
    ['docs/ops/release-image-smoke.en.md MAILER_IMAGE_TAG required note', releaseSmokeDocEn],
  ]) {
    assertContains(source, '`MAILER_IMAGE_TAG`', label);
    assertContains(source, '`MAILER_IMAGE_DIGEST`', label);
  }

  for (const [label, source] of [
    ['README.md published image example tag', readmeJa],
    ['README.en.md published image example tag', readmeEn],
  ]) {
    assertEqual(
      label,
      firstMatch(source, /ghcr\.io\/kooiei-in4a\/amane-mailer:(v[\d.]+)/, label),
      expectedImageTag,
    );
  }

  for (const [label, source, marker] of [
    [
      'docs/ops/setup-guide.md current recommendation',
      setupGuideJa,
      /\*\*現行推奨:\*\*[^\n]*\bv([\d.]+)\b/,
    ],
    [
      'docs/ops/setup-guide.en.md current recommendation',
      setupGuideEn,
      /\*\*Current recommendation:\*\*[^\n]*\bv([\d.]+)\b/i,
    ],
  ]) {
    assertContains(source, 'release/current-public.json', `${label} authority link`);
    assertMarkedVersion(source, marker, authority.version, label);
    assertCurrentVersionLines(source, authority.version, label);
  }

  assertContains(roadmap, 'release/current-public.json', 'ROADMAP.md release authority link');
  assertMarkedVersion(
    roadmap,
    /current public stable line is \*\*v([\d.]+)\*\*/i,
    authority.version,
    'ROADMAP.md current stable line',
  );
  assertCurrentVersionLines(roadmap, authority.version, 'ROADMAP.md');

  assertContains(agents, 'global.json', 'AGENTS.md SDK source of truth');
  assertNoMatch(
    agents,
    /\.NET SDK:[^\n]*\b\d+\.\d+\.\d+\b/i,
    'AGENTS.md duplicated SDK version',
  );
  for (const [label, source] of [
    ['README.md SDK source of truth', readmeJa],
    ['README.en.md SDK source of truth', readmeEn],
  ]) {
    assertContains(source, 'global.json', label);
    assertNoMatch(source, /\.NET SDK[^\n]*\b\d+\.\d+\.\d+\b/i, `${label} duplicated SDK version`);
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

  const governedCommandSources = [
    ['README.md', readmeJa],
    ['README.en.md', readmeEn],
    ['docs/ops/release-image-smoke.md', releaseSmokeDocJa],
    ['docs/ops/release-image-smoke.en.md', releaseSmokeDocEn],
  ];
  for (const [label, source] of governedCommandSources) {
    assertReleaseSmokeCommandExamples(source, expectedImageTag, label);
  }

  assertContains(
    security,
    `| ${authority.version}   | Yes (latest release) |`,
    'SECURITY.md supported version table',
  );

  if (!existsSync(path.join(root, releaseRecordPath))) {
    fail(`Missing release record for ${expectedImageTag}: ${releaseRecordPath}.`);
  } else {
    const expectedSmokeReleaseLink = `[${releaseRecordPath}](../releases/${expectedImageTag}.md)`;
    for (const [label, source] of [
      ['docs/ops/release-image-smoke.md recorded smoke results link', releaseSmokeDocJa],
      ['docs/ops/release-image-smoke.en.md recorded smoke results link', releaseSmokeDocEn],
    ]) {
      assertContains(source, expectedSmokeReleaseLink, label);
    }
  }

  reportAndExit();

  console.log(
    `Current public release alignment check passed: authority ${expectedImageTag}, README, setup guides, `
      + `ROADMAP, release smoke docs, and SECURITY are aligned; release-smoke scripts/compose have no implicit Mailer tag defaults.`,
  );
}

function expectFailure(label, source, expectedTag, needle) {
  errors.length = 0;
  assertReleaseSmokeCommandExamples(source, expectedTag, label);
  if (errors.length === 0) {
    throw new Error(`${label}: expected failure but passed`);
  }
  if (!errors.some((message) => message.includes(needle))) {
    throw new Error(`${label}: expected error containing '${needle}', got: ${errors.join('; ')}`);
  }
}

function expectPass(label, source, expectedTag) {
  errors.length = 0;
  assertReleaseSmokeCommandExamples(source, expectedTag, label);
  if (errors.length > 0) {
    throw new Error(`${label}: expected pass but failed: ${errors.join('; ')}`);
  }
}

function runSelfTest() {
  const expectedTag = 'v2.1.0';
  const staleTag = 'v1.3.8';

  expectPass(
    'positive current command',
    `MAILER_IMAGE_TAG=${expectedTag} bash scripts/release-smoke.sh\n`,
    expectedTag,
  );
  expectFailure(
    'stale command',
    `MAILER_IMAGE_TAG=${staleTag} bash scripts/release-smoke.sh\n`,
    expectedTag,
    staleTag,
  );
  expectFailure(
    'missing command',
    'Use MAILER_IMAGE_DIGEST=sha256:abc when needed.\n',
    expectedTag,
    'missing an explicit MAILER_IMAGE_TAG',
  );
  expectFailure(
    'contradictory commands',
    `MAILER_IMAGE_TAG=${expectedTag} bash scripts/release-smoke.sh\nMAILER_IMAGE_TAG=${staleTag} bash scripts/release-smoke.sh\n`,
    expectedTag,
    staleTag,
  );

  // Historical release records are outside the governed follower set; a stale
  // example there must not be treated as a current-command failure by itself.
  const historical = `# historical\nMAILER_IMAGE_TAG=${staleTag} bash scripts/release-smoke.sh\n`;
  const historicalTags = collectReleaseSmokeCommandTags(historical);
  if (historicalTags.length !== 1 || historicalTags[0] !== staleTag) {
    throw new Error('historical fixture did not retain stale tag for exemption check');
  }
  if (governedReleaseSmokeCommandExamplePaths.includes('docs/releases/v1.3.8.md')) {
    throw new Error('historical release records must not be in governed command followers');
  }

  const fixtureRoot = mkdtempSync(path.join(tmpdir(), 'amane-release-smoke-tag-drift-'));
  try {
    for (const relativePath of governedReleaseSmokeCommandExamplePaths) {
      const absolutePath = path.join(fixtureRoot, relativePath);
      mkdirSync(path.dirname(absolutePath), { recursive: true });
      writeFileSync(
        absolutePath,
        `MAILER_IMAGE_TAG=${staleTag} bash scripts/release-smoke.sh\n`,
        'utf8',
      );
      const source = readFileSync(absolutePath, 'utf8');
      expectFailure(
        `fixture ${relativePath}`,
        source,
        expectedTag,
        staleTag,
      );
    }
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }

  console.log(
    'check-release-smoke-tag-drift self-test passed: positive, stale, missing, contradictory, and historical-exemption cases.',
  );
}

const args = process.argv.slice(2);
if (args.includes('--self-test')) {
  runSelfTest();
} else if (args.length > 0) {
  console.error(`Unknown arguments: ${args.join(' ')}`);
  console.error('Usage: node scripts/check-release-smoke-tag-drift.mjs [--self-test]');
  process.exit(1);
} else {
  runRepositoryCheck();
}
