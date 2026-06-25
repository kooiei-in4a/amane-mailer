#!/usr/bin/env node

import { readFileSync } from 'node:fs';

const filePath = process.argv[2] ?? 'docs/api/openapi.yaml';
const source = readFileSync(filePath, 'utf8');
const lines = source.split(/\r?\n/);

const errors = [];
const pointers = new Set(['#']);
const refs = [];
const topLevelKeys = new Set();
const operationIds = new Map();
const stack = [];

function fail(message, lineNumber) {
  const suffix = lineNumber === undefined ? '' : ` (line ${lineNumber})`;
  errors.push(`${message}${suffix}`);
}

function pointerEscape(segment) {
  return segment.replaceAll('~', '~0').replaceAll('/', '~1');
}

function unquote(value) {
  const trimmed = value.trim();
  if (
    (trimmed.startsWith('"') && trimmed.endsWith('"')) ||
    (trimmed.startsWith("'") && trimmed.endsWith("'"))
  ) {
    return trimmed.slice(1, -1);
  }

  return trimmed;
}

function normalizeScalar(value) {
  return unquote(value.split('#', 1)[0].trim()).replace(/,$/, '');
}

function mappingKeyFrom(text) {
  const match = text.match(/^((?:"[^"]+")|(?:'[^']+')|[^:#{}\[\],]+):(?:\s|$)/);
  return match ? unquote(match[1]) : undefined;
}

for (const [index, line] of lines.entries()) {
  const lineNumber = index + 1;

  if (/^\s*$/.test(line) || /^\s*#/.test(line)) {
    continue;
  }

  const indentMatch = line.match(/^ */);
  const indent = indentMatch ? indentMatch[0].length : 0;
  const trimmed = line.trim();

  if (line.includes('\t')) {
    fail('YAML indentation must use spaces, not tabs', lineNumber);
  }

  if (indent % 2 !== 0) {
    fail('YAML indentation must use two-space levels', lineNumber);
  }

  for (const refMatch of line.matchAll(/\$ref:\s*['"]?(#[^'"\s}\]]+)/g)) {
    refs.push({ ref: refMatch[1], lineNumber });
  }

  const operationMatch = trimmed.match(/^operationId:\s*(.+)$/);
  if (operationMatch) {
    const operationId = normalizeScalar(operationMatch[1]);
    const previousLine = operationIds.get(operationId);
    if (previousLine !== undefined) {
      fail(`Duplicate operationId '${operationId}' also appears on line ${previousLine}`, lineNumber);
    } else {
      operationIds.set(operationId, lineNumber);
    }
  }

  const listItem = trimmed.startsWith('- ');
  const mappingText = listItem ? trimmed.slice(2).trimStart() : trimmed;
  const key = mappingKeyFrom(mappingText);

  if (key === undefined) {
    continue;
  }

  const keyIndent = listItem ? indent + 2 : indent;
  while (stack.length > 0 && stack[stack.length - 1].indent >= keyIndent) {
    stack.pop();
  }

  const path = [...stack.map((entry) => entry.key), key];
  const pointer = `#/${path.map(pointerEscape).join('/')}`;
  pointers.add(pointer);

  if (keyIndent === 0) {
    topLevelKeys.add(key);
  }

  stack.push({ indent: keyIndent, key });
}

if (!/^openapi:\s*['"]?3\.1\.0['"]?\s*$/m.test(source)) {
  fail('Expected top-level openapi: 3.1.0');
}

for (const requiredKey of ['openapi', 'info', 'paths', 'components']) {
  if (!topLevelKeys.has(requiredKey)) {
    fail(`Missing top-level '${requiredKey}' section`);
  }
}

for (const requiredPointer of [
  '#/components/securitySchemes',
  '#/components/schemas',
]) {
  if (!pointers.has(requiredPointer)) {
    fail(`Missing required OpenAPI section ${requiredPointer}`);
  }
}

for (const { ref, lineNumber } of refs) {
  if (!ref.startsWith('#/')) {
    fail(`Only internal OpenAPI references are allowed: ${ref}`, lineNumber);
    continue;
  }

  if (!pointers.has(ref)) {
    fail(`Unresolved OpenAPI reference ${ref}`, lineNumber);
  }
}

if (operationIds.size === 0) {
  fail('Expected at least one operationId');
}

if (errors.length > 0) {
  console.error(`OpenAPI validation failed for ${filePath}:`);
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(
  `OpenAPI validation passed for ${filePath}: ${refs.length} refs, ${operationIds.size} operations.`,
);
