#!/usr/bin/env node

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { performance } from 'node:perf_hooks';

const root = process.cwd();
const defaultManifestPath = 'docs/implementation-status.json';

const ALLOWED_STATUSES = new Set([
  'planned',
  'partial',
  'implemented',
  'deferred',
  'removed',
]);
const REQUIRED_FEATURE_FIELDS = [
  'id',
  'status',
  'trackingIssue',
  'lastVerified',
  'relatedAdrs',
];
const FEATURE_ALLOWED_KEYS = new Set([
  ...REQUIRED_FEATURE_FIELDS,
  'notes',
  'resolution',
  'supersededBy',
  'evidence',
]);
const EVIDENCE_ALLOWED_KEYS = new Set([
  'files',
  'types',
  'services',
  'diRegistrations',
  'endpoints',
  'uiActions',
  'tests',
]);
const FEATURE_ID_PATTERN = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const ADR_NUMBER_PATTERN = /^\d{4}$/;
const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const ADR_FORBIDDEN_PATTERNS = [
  {
    pattern: /## 実装ステータス（\d{4}-\d{2}-\d{2}/,
    description: 'dated "## 実装ステータス" section header',
  },
  {
    pattern: /\*\*実装ステータス（\d{4}-\d{2}-\d{2}）:\*\*/,
    description: 'inline dated implementation status block',
  },
  {
    pattern: /実装ステータス\d{4}-\d{2}-\d{2}-時点/,
    description: 'dated implementation status anchor',
  },
  {
    pattern: /0013-admin-threat-model-and-pii-policy\.md#実装ステータス/,
    description: 'ADR 0013 status anchor link',
  },
  {
    pattern: /ADR 0013 実装ステータス表/,
    description: 'ADR 0013 status table reference for current status',
  },
];

function fail(errors, message) {
  errors.push(message);
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function loadAdrNumbers() {
  const adrDir = path.join(root, 'docs/adr');
  const numbers = new Set();

  for (const fileName of readdirSync(adrDir)) {
    const match = fileName.match(/^(\d{4})-.*\.md$/);
    if (match) {
      numbers.add(match[1]);
    }
  }

  return numbers;
}

function validateManifest(manifestPath, errors) {
  const absolutePath = path.isAbsolute(manifestPath)
    ? manifestPath
    : path.join(root, manifestPath);

  if (!existsSync(absolutePath)) {
    fail(errors, `Manifest file is missing: ${manifestPath}.`);
    return;
  }

  let manifest;
  try {
    manifest = JSON.parse(readFileSync(absolutePath, 'utf8'));
  } catch (error) {
    fail(errors, `Manifest JSON syntax error in ${manifestPath}: ${error.message}`);
    return;
  }

  if (!isPlainObject(manifest)) {
    fail(errors, `Manifest root must be a JSON object: ${manifestPath}.`);
    return;
  }

  if (typeof manifest.version !== 'number' || !Number.isInteger(manifest.version) || manifest.version < 1) {
    fail(errors, `Manifest version must be a positive integer: ${manifestPath}.`);
  }

  if (!Array.isArray(manifest.features)) {
    fail(errors, `Manifest features must be an array: ${manifestPath}.`);
    return;
  }

  const adrNumbers = loadAdrNumbers();
  const seenIds = new Map();
  const knownIds = new Set();

  for (let index = 0; index < manifest.features.length; index += 1) {
    const feature = manifest.features[index];
    if (!isPlainObject(feature)) {
      fail(errors, 'Each feature entry must be a JSON object.');
      continue;
    }

    const label = typeof feature.id === 'string' ? `feature '${feature.id}'` : 'feature entry';

    for (const key of Object.keys(feature)) {
      if (!FEATURE_ALLOWED_KEYS.has(key)) {
        fail(errors, `${label} has unsupported field '${key}'.`);
      }
    }

    for (const field of REQUIRED_FEATURE_FIELDS) {
      if (!(field in feature)) {
        fail(errors, `${label} is missing required field '${field}'.`);
      }
    }

    if (typeof feature.id !== 'string' || feature.id.length === 0) {
      fail(errors, `${label} field 'id' must be a non-empty string.`);
    } else if (!FEATURE_ID_PATTERN.test(feature.id)) {
      fail(errors, `${label} field 'id' must use kebab-case: '${feature.id}'.`);
    } else if (seenIds.has(feature.id)) {
      fail(errors, `Duplicate feature id '${feature.id}' also appears at index ${seenIds.get(feature.id)}.`);
    } else {
      seenIds.set(feature.id, index);
      knownIds.add(feature.id);
    }

    if (typeof feature.status !== 'string' || !ALLOWED_STATUSES.has(feature.status)) {
      fail(
        errors,
        `${label} has invalid status '${feature.status ?? '(missing)'}'. `
        + `Allowed values: ${[...ALLOWED_STATUSES].join(', ')}.`,
      );
    }

    if (
      typeof feature.trackingIssue !== 'number'
      || !Number.isInteger(feature.trackingIssue)
      || feature.trackingIssue <= 0
    ) {
      fail(errors, `${label} field 'trackingIssue' must be a positive integer.`);
    }

    if (typeof feature.lastVerified !== 'string' || !DATE_PATTERN.test(feature.lastVerified)) {
      fail(errors, `${label} field 'lastVerified' must use YYYY-MM-DD format.`);
    }

    if (!Array.isArray(feature.relatedAdrs) || feature.relatedAdrs.length === 0) {
      fail(errors, `${label} field 'relatedAdrs' must be a non-empty array.`);
    } else {
      for (const adrRef of feature.relatedAdrs) {
        if (typeof adrRef !== 'string' || !ADR_NUMBER_PATTERN.test(adrRef)) {
          fail(errors, `${label} relatedAdrs entry '${adrRef}' must be a four-digit ADR number.`);
          continue;
        }

        if (!adrNumbers.has(adrRef)) {
          fail(errors, `${label} relatedAdrs entry '${adrRef}' does not match any file in docs/adr/.`);
        }
      }
    }

    if ('notes' in feature && typeof feature.notes !== 'string') {
      fail(errors, `${label} field 'notes' must be a string when present.`);
    }

    if ('resolution' in feature) {
      validateResolution(feature, label, errors);
    }

    if ('supersededBy' in feature) {
      validateSupersededByShape(feature, label, errors);
    }

    if ('evidence' in feature) {
      validateEvidence(feature, label, errors);
    }

    validateStateContradictions(feature, label, errors);
  }

  for (const feature of manifest.features) {
    if (!isPlainObject(feature) || typeof feature.id !== 'string') {
      continue;
    }

    if ('supersededBy' in feature) {
      validateSupersededByTarget(feature, `feature '${feature.id}'`, errors, knownIds);
    }
  }
}

function validateResolution(feature, label, errors) {
  const { resolution } = feature;

  if (typeof resolution === 'string') {
    if (resolution.trim().length === 0) {
      fail(errors, `${label} field 'resolution' must not be empty when present.`);
    }
    return;
  }

  if (!isPlainObject(resolution)) {
    fail(errors, `${label} field 'resolution' must be a string or object when present.`);
    return;
  }

  if (typeof resolution.reason !== 'string' || resolution.reason.trim().length === 0) {
    fail(errors, `${label} field 'resolution.reason' must be a non-empty string when resolution is an object.`);
  }

  for (const [key, value] of Object.entries(resolution)) {
    if (key === 'reason' || key === 'reference') {
      if (typeof value !== 'string' || value.trim().length === 0) {
        fail(errors, `${label} field 'resolution.${key}' must be a non-empty string when present.`);
      }
      continue;
    }

    fail(errors, `${label} field 'resolution' has unsupported key '${key}'.`);
  }
}

function validateSupersededByShape(feature, label, errors) {
  const { supersededBy } = feature;

  if (typeof supersededBy !== 'string' || supersededBy.length === 0) {
    fail(errors, `${label} field 'supersededBy' must be a non-empty string when present.`);
    return;
  }

  if (!FEATURE_ID_PATTERN.test(supersededBy)) {
    fail(errors, `${label} field 'supersededBy' must reference a kebab-case feature id.`);
  }

  if (supersededBy === feature.id) {
    fail(errors, `${label} field 'supersededBy' must not reference itself.`);
  }
}

function validateSupersededByTarget(feature, label, errors, knownIds) {
  const { supersededBy } = feature;

  if (typeof supersededBy !== 'string' || supersededBy.length === 0) {
    return;
  }

  if (!knownIds.has(supersededBy)) {
    fail(errors, `${label} field 'supersededBy' references unknown feature id '${supersededBy}'.`);
  }
}

function validateEvidence(feature, label, errors) {
  const { evidence } = feature;

  if (!isPlainObject(evidence)) {
    fail(errors, `${label} field 'evidence' must be an object when present.`);
    return;
  }

  if (Object.keys(evidence).length === 0) {
    fail(errors, `${label} field 'evidence' must not be empty when present.`);
    return;
  }

  for (const [key, value] of Object.entries(evidence)) {
    if (!EVIDENCE_ALLOWED_KEYS.has(key)) {
      fail(errors, `${label} evidence has unsupported key '${key}'.`);
      continue;
    }

    if (!Array.isArray(value) || value.length === 0) {
      fail(errors, `${label} evidence '${key}' must be a non-empty array.`);
      continue;
    }

    for (const entry of value) {
      if (typeof entry !== 'string' || entry.trim().length === 0) {
        fail(errors, `${label} evidence '${key}' entries must be non-empty strings.`);
      }
    }
  }
}

function validateStateContradictions(feature, label, errors) {
  const status = feature.status;
  const hasResolution = 'resolution' in feature;
  const hasSupersededBy = 'supersededBy' in feature;
  const terminalStatus = status === 'deferred' || status === 'removed';

  if ((hasResolution || hasSupersededBy) && !terminalStatus) {
    fail(
      errors,
      `${label} cannot set resolution/supersededBy when status is '${status}'. `
      + 'Use deferred or removed.',
    );
  }
}

function validateAdrStatusDuplication(errors) {
  const adrDir = path.join(root, 'docs/adr');

  for (const fileName of readdirSync(adrDir)) {
    if (!fileName.endsWith('.md')) {
      continue;
    }

    const relativePath = path.join('docs/adr', fileName).replaceAll('\\', '/');
    const source = readFileSync(path.join(root, relativePath), 'utf8');

    for (const { pattern, description } of ADR_FORBIDDEN_PATTERNS) {
      if (pattern.test(source)) {
        fail(errors, `${relativePath} matches forbidden current-status duplication pattern: ${description}.`);
      }
    }
  }
}

function runCheck(manifestPath = defaultManifestPath) {
  const errors = [];
  validateManifest(manifestPath, errors);
  validateAdrStatusDuplication(errors);
  return errors;
}

function runSelfTest() {
  const cases = [
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-missing-field.json',
      needle: "missing required field 'trackingIssue'",
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-status.json',
      needle: 'invalid status',
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-duplicate-id.json',
      needle: "Duplicate feature id 'sample-feature'",
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-adr-reference.json',
      needle: "relatedAdrs entry '9999'",
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-tracking-issue.json',
      needle: 'trackingIssue',
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-state-contradiction.json',
      needle: 'cannot set resolution/supersededBy',
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-evidence.json',
      needle: "evidence has unsupported key 'widgets'",
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-json-syntax.json',
      needle: 'Manifest JSON syntax error',
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-adr-format.json',
      needle: "relatedAdrs entry '13'",
    },
    {
      fixture: 'scripts/fixtures/implementation-status/invalid-unknown-key.json',
      needle: "unsupported field 'supercededBy'",
    },
  ];

  const failures = [];

  const validErrors = runCheck(defaultManifestPath);
  if (validErrors.length > 0) {
    failures.push(`expected ${defaultManifestPath} to pass, but got: ${validErrors.join('; ')}`);
  }

  for (const testCase of cases) {
    const errors = runCheck(testCase.fixture);
    if (errors.length === 0) {
      failures.push(`expected ${testCase.fixture} to fail`);
      continue;
    }

    if (!errors.some((error) => error.includes(testCase.needle))) {
      failures.push(
        `expected ${testCase.fixture} to fail with '${testCase.needle}', but got: ${errors.join('; ')}`,
      );
    }
  }

  if (failures.length > 0) {
    console.error('Implementation status self-test failed:');
    for (const failure of failures) {
      console.error(`- ${failure}`);
    }
    process.exit(1);
  }

  console.log(`Implementation status self-test passed (${cases.length} invalid fixtures).`);
}

function main() {
  const args = process.argv.slice(2);

  if (args.length === 1 && args[0] === '--self-test') {
    runSelfTest();
    return;
  }

  const manifestPath = args[0] ?? defaultManifestPath;
  const started = performance.now();
  const errors = runCheck(manifestPath);
  const elapsedMs = performance.now() - started;

  if (errors.length > 0) {
    console.error(`Implementation status check failed for ${manifestPath}:`);
    for (const error of errors) {
      console.error(`- ${error}`);
    }
    console.error(`Elapsed: ${elapsedMs.toFixed(1)}ms`);
    process.exit(1);
  }

  console.log(
    `Implementation status check passed for ${manifestPath} `
    + `(features validated, ADR duplication patterns checked). `
    + `Elapsed: ${elapsedMs.toFixed(1)}ms`,
  );
}

main();
