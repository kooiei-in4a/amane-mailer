#!/usr/bin/env node

/**
 * Compare MailRequestCreateRequest JSON field inventories across Contracts,
 * OpenAPI, payload_hash classification, and Python / TypeScript SDKs.
 *
 * Usage:
 *   node scripts/check-mail-request-field-inventory.mjs
 *   node scripts/check-mail-request-field-inventory.mjs --self-test
 */

import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const root = process.cwd();
const defaultExceptionsPath = 'scripts/mail-request-field-inventory-exceptions.json';

const defaultPaths = {
  requestDto: 'src/Amane.Mailer.Contracts/MailRequests/MailRequestCreateRequest.cs',
  openApi: 'docs/api/openapi.yaml',
  openApiSchemaName: 'MailRequestCreateRequest',
  payloadHashContract: 'src/Amane.Mailer.Contracts/Security/MailPayloadHashContract.cs',
  pythonBuilder: 'sdk/python/src/amane_mailer/builder.py',
  pythonValidation: 'sdk/python/src/amane_mailer/validation.py',
  pythonPayloadHash: 'sdk/python/src/amane_mailer/payload_hash.py',
  tsBuilder: 'sdk/typescript/src/builder.mjs',
  tsValidation: 'sdk/typescript/src/validation.mjs',
  tsPayloadHash: 'sdk/typescript/src/payload-hash.mjs',
  exceptions: defaultExceptionsPath,
};

function resolvePath(relativeOrAbsolute) {
  return path.isAbsolute(relativeOrAbsolute)
    ? relativeOrAbsolute
    : path.join(root, relativeOrAbsolute);
}

function read(relativeOrAbsolute) {
  return readFileSync(resolvePath(relativeOrAbsolute), 'utf8');
}

function uniqueSorted(values) {
  return [...new Set(values)].sort();
}

function sameSet(actual, expected) {
  const actualSorted = uniqueSorted(actual);
  const expectedSorted = uniqueSorted(expected);
  return (
    actualSorted.length === expectedSorted.length
    && actualSorted.every((value, index) => value === expectedSorted[index])
  );
}

function formatSetDiff(expected, actual) {
  const expectedSet = new Set(expected);
  const actualSet = new Set(actual);
  const missing = uniqueSorted([...expectedSet].filter((value) => !actualSet.has(value)));
  const extra = uniqueSorted([...actualSet].filter((value) => !expectedSet.has(value)));
  const parts = [];
  if (missing.length > 0) {
    parts.push(`missing=[${missing.join(', ')}]`);
  }
  if (extra.length > 0) {
    parts.push(`extra=[${extra.join(', ')}]`);
  }
  return parts.join(' ') || 'sets differ';
}

function assertSameSet(errors, label, actual, expected) {
  if (sameSet(actual, expected)) {
    return;
  }

  errors.push(
    `${label} drifted. expected=[${uniqueSorted(expected).join(', ')}] `
    + `actual=[${uniqueSorted(actual).join(', ')}] `
    + `(${formatSetDiff(expected, actual)})`,
  );
}

function regexEscape(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function lineIndent(line) {
  return line.match(/^ */)?.[0].length ?? 0;
}

function isBlankOrComment(line) {
  return /^\s*(?:#.*)?$/.test(line);
}

function parseInlineList(value) {
  const trimmed = value.trim();
  if (!trimmed.startsWith('[') || !trimmed.endsWith(']')) {
    return undefined;
  }

  return trimmed
    .slice(1, -1)
    .split(',')
    .map((item) => item.trim().replace(/^["']|["']$/g, ''))
    .filter(Boolean);
}

function findDirectChildSection(lines, parentIndent, key) {
  const childIndent = parentIndent + 2;
  const pattern = new RegExp(`^ {${childIndent}}${regexEscape(key)}:\\s*(.*)$`);
  const start = lines.findIndex((line) => pattern.test(line));
  if (start < 0) {
    return undefined;
  }

  const value = lines[start].match(pattern)?.[1].trim() ?? '';
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (isBlankOrComment(lines[index])) {
      continue;
    }

    if (lineIndent(lines[index]) <= childIndent) {
      end = index;
      break;
    }
  }

  return {
    indent: childIndent,
    value,
    lines: lines.slice(start + 1, end),
  };
}

function extractSchemaBlock(source, schemaName, errors) {
  const lines = source.split(/\r?\n/);
  const schemaPattern = new RegExp(`^( *)${regexEscape(schemaName)}:\\s*$`);
  const start = lines.findIndex((line) => schemaPattern.test(line));
  if (start < 0) {
    errors.push(`OpenAPI schema ${schemaName} is missing.`);
    return {
      baseIndent: 0,
      lines: [],
    };
  }

  const baseIndent = lineIndent(lines[start]);
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (isBlankOrComment(lines[index])) {
      continue;
    }

    if (lineIndent(lines[index]) <= baseIndent) {
      end = index;
      break;
    }
  }

  return {
    baseIndent,
    lines: lines.slice(start + 1, end),
  };
}

function parseRequired(schema) {
  const section = findDirectChildSection(schema.lines, schema.baseIndent, 'required');
  if (!section) {
    return [];
  }

  const inline = parseInlineList(section.value);
  if (inline) {
    return inline;
  }

  const required = [];
  for (const line of section.lines) {
    const match = line.match(/^\s*-\s+([A-Za-z0-9_]+)\s*$/);
    if (!match) {
      if (isBlankOrComment(line)) {
        continue;
      }

      break;
    }

    required.push(match[1]);
  }

  return required;
}

function isOpenApiNullable(propertyBlock) {
  return (
    /^\s*nullable:\s*true\s*$/m.test(propertyBlock)
    || /^\s*type:\s*\[[^\]]*\bnull\b[^\]]*\]\s*$/m.test(propertyBlock)
  );
}

function parseProperties(schema) {
  const section = findDirectChildSection(schema.lines, schema.baseIndent, 'properties');
  const lines = section?.lines ?? [];
  const propertyIndent = section ? section.indent + 2 : 0;
  const properties = new Map();

  for (let index = 0; index < lines.length; index += 1) {
    const match = lines[index].match(new RegExp(`^ {${propertyIndent}}([A-Za-z0-9_]+):\\s*$`));
    if (!match) {
      continue;
    }

    const name = match[1];
    let end = lines.length;
    for (let next = index + 1; next < lines.length; next += 1) {
      if (isBlankOrComment(lines[next])) {
        continue;
      }

      if (lineIndent(lines[next]) <= propertyIndent) {
        end = next;
        break;
      }
    }

    const block = lines.slice(index, end).join('\n');
    properties.set(name, {
      name,
      block,
      nullable: isOpenApiNullable(block),
    });
  }

  return properties;
}

function parseJsonDto(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`DTO file is missing: ${relativePath}.`);
    return { properties: [] };
  }

  const source = read(relativePath);
  const propertyMatches = source.matchAll(
    /\[JsonPropertyName\("([^"]+)"\)\]\s*public\s+(required\s+)?(.+?)\s+([A-Za-z_]\w*)\s*\{\s*get;\s*init;\s*\}/gs,
  );
  const properties = [...propertyMatches].map((match) => {
    const type = match[3].replace(/\s+/g, ' ').trim();
    return {
      jsonName: match[1],
      required: match[2] !== undefined,
      nullable: type.includes('?'),
      type,
      clrName: match[4],
    };
  });

  if (properties.length === 0) {
    errors.push(`No JSON properties found in ${relativePath} (fail-closed).`);
  }

  return { relativePath, source, properties };
}

function parseStringArray(relativePath, propertyName, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`File is missing: ${relativePath}.`);
    return [];
  }

  const source = read(relativePath);
  const pattern = new RegExp(
    `public\\s+static\\s+readonly\\s+string\\[\\]\\s+${propertyName}\\s*=\\s*\\[(.*?)\\];`,
    's',
  );
  const match = source.match(pattern);
  if (!match) {
    errors.push(`Could not find ${propertyName} in ${relativePath} (fail-closed).`);
    return [];
  }

  return [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
}

function parseQuotedStringSet(source, declarationPattern, label, errors) {
  const match = source.match(declarationPattern);
  if (!match) {
    errors.push(`Could not parse ${label} (fail-closed).`);
    return [];
  }

  const fields = [...match[1].matchAll(/["']([a-z][a-z0-9_]*)["']/g)].map((item) => item[1]);
  if (fields.length === 0) {
    errors.push(`${label} parsed empty (fail-closed).`);
  }

  return fields;
}

function parsePythonBuilderInventory(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`Python builder is missing: ${relativePath}.`);
    return { fields: [], nullableFields: [], omitNullFields: [] };
  }

  const source = read(relativePath);
  const fields = uniqueSorted([
    ...source.matchAll(/self\._fields\["([a-z][a-z0-9_]*)"\]\s*=/g),
  ].map((match) => match[1]));

  if (source.includes('draft["payload_hash"]') || source.includes("draft['payload_hash']")) {
    fields.push('payload_hash');
  }

  if (fields.length === 0) {
    errors.push(`No Python builder JSON fields found in ${relativePath} (fail-closed).`);
  }

  const nullableFields = uniqueSorted([
    ...source.matchAll(/self\._explicit_nulls\.(?:add|discard)\(\s*["']([a-z][a-z0-9_]*)["']\s*\)/g),
  ].map((match) => match[1]));

  if (nullableFields.length === 0) {
    errors.push(
      `No Python builder explicit-null fields found in ${relativePath} (fail-closed).`,
    );
  }

  const omitMatch = source.match(
    /for\s+key\s+in\s*\(\s*((?:["'][a-z][a-z0-9_]*["']\s*,?\s*)+)\s*\)/,
  );
  const omitNullFields = omitMatch
    ? [...omitMatch[1].matchAll(/["']([a-z][a-z0-9_]*)["']/g)].map((item) => item[1])
    : [];

  if (!omitMatch) {
    errors.push(
      `Could not parse Python builder omit-null field tuple in ${relativePath} (fail-closed).`,
    );
  }

  return {
    fields: uniqueSorted(fields),
    nullableFields: uniqueSorted(nullableFields),
    omitNullFields: uniqueSorted(omitNullFields),
  };
}

function parseTypeScriptBuilderInventory(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`TypeScript builder is missing: ${relativePath}.`);
    return { fields: [], nullableFields: [], omitNullFields: [] };
  }

  const source = read(relativePath);
  const fields = uniqueSorted([
    ...source.matchAll(/this\.#fields\.([a-z][a-z0-9_]*)\s*=/g),
  ].map((match) => match[1]));

  if (/\bdraft\.payload_hash\s*=/.test(source)) {
    fields.push('payload_hash');
  }

  if (fields.length === 0) {
    errors.push(`No TypeScript builder JSON fields found in ${relativePath} (fail-closed).`);
  }

  const nullableFields = uniqueSorted([
    ...source.matchAll(/this\.#explicitNulls\.(?:add|delete)\(\s*['"]([a-z][a-z0-9_]*)['"]\s*\)/g),
  ].map((match) => match[1]));

  if (nullableFields.length === 0) {
    errors.push(
      `No TypeScript builder explicit-null fields found in ${relativePath} (fail-closed).`,
    );
  }

  const omitMatch = source.match(
    /for\s*\(\s*const\s+key\s+of\s*\[\s*((?:['"][a-z][a-z0-9_]*['"]\s*,?\s*)+)\s*\]/,
  );
  const omitNullFields = omitMatch
    ? [...omitMatch[1].matchAll(/['"]([a-z][a-z0-9_]*)['"]/g)].map((item) => item[1])
    : [];

  if (!omitMatch) {
    errors.push(
      `Could not parse TypeScript builder omit-null field array in ${relativePath} (fail-closed).`,
    );
  }

  return {
    fields: uniqueSorted(fields),
    nullableFields: uniqueSorted(nullableFields),
    omitNullFields: uniqueSorted(omitNullFields),
  };
}

function parsePythonValidationFields(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`Python validation is missing: ${relativePath}.`);
    return [];
  }

  const source = read(relativePath);
  return parseQuotedStringSet(
    source,
    /MAIL_REQUEST_JSON_FIELDS\s*=\s*frozenset\s*\(\s*(?:\{|\()([\s\S]*?)(?:\}|\))\s*\)/,
    `Python MAIL_REQUEST_JSON_FIELDS in ${relativePath}`,
    errors,
  );
}

function parseTypeScriptValidationFields(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`TypeScript validation is missing: ${relativePath}.`);
    return [];
  }

  const source = read(relativePath);
  return parseQuotedStringSet(
    source,
    /MAIL_REQUEST_JSON_FIELDS\s*=\s*new\s+Set\s*\(\s*\[([\s\S]*?)\]\s*\)/,
    `TypeScript MAIL_REQUEST_JSON_FIELDS in ${relativePath}`,
    errors,
  );
}

function parsePythonIncludedFields(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`Python payload_hash is missing: ${relativePath}.`);
    return [];
  }

  const source = read(relativePath);
  return parseQuotedStringSet(
    source,
    /INCLUDED_FIELDS\s*=\s*frozenset\s*\(\s*(?:\[|\()([\s\S]*?)(?:\]|\))\s*\)/,
    `Python INCLUDED_FIELDS in ${relativePath}`,
    errors,
  );
}

function parseTypeScriptIncludedFields(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    errors.push(`TypeScript payload_hash is missing: ${relativePath}.`);
    return [];
  }

  const source = read(relativePath);
  return parseQuotedStringSet(
    source,
    /INCLUDED_FIELDS\s*=\s*new\s+Set\s*\(\s*\[([\s\S]*?)\]\s*\)/,
    `TypeScript INCLUDED_FIELDS in ${relativePath}`,
    errors,
  );
}

function loadExceptions(relativePath, errors) {
  if (!existsSync(resolvePath(relativePath))) {
    return [];
  }

  let document;
  try {
    document = JSON.parse(read(relativePath));
  } catch (error) {
    errors.push(`Exceptions JSON syntax error in ${relativePath}: ${error.message}`);
    return [];
  }

  if (!document || typeof document !== 'object' || Array.isArray(document)) {
    errors.push(`Exceptions root must be a JSON object: ${relativePath}.`);
    return [];
  }

  if (!Array.isArray(document.exceptions)) {
    errors.push(`Exceptions file must include an 'exceptions' array: ${relativePath}.`);
    return [];
  }

  const normalized = [];
  for (const [index, entry] of document.exceptions.entries()) {
    const label = `exceptions[${index}]`;
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      errors.push(`${label} must be an object.`);
      continue;
    }

    if (typeof entry.field !== 'string' || entry.field.length === 0) {
      errors.push(`${label}.field must be a non-empty string.`);
    }
    if (typeof entry.layer !== 'string' || entry.layer.length === 0) {
      errors.push(`${label}.layer must be a non-empty string.`);
    }
    if (typeof entry.reason !== 'string' || entry.reason.length === 0) {
      errors.push(`${label}.reason must be a non-empty string.`);
    }
    if (typeof entry.trackingIssue !== 'number' || !Number.isInteger(entry.trackingIssue)) {
      errors.push(`${label}.trackingIssue must be an integer issue number.`);
    }
    if (typeof entry.expiresOn !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(entry.expiresOn)) {
      errors.push(`${label}.expiresOn must be YYYY-MM-DD.`);
    } else if (entry.expiresOn < new Date().toISOString().slice(0, 10)) {
      errors.push(
        `${label} for field '${entry.field}' expired on ${entry.expiresOn}; remove or renew it.`,
      );
    }

    normalized.push(entry);
  }

  return normalized;
}

function exceptionCovers(exceptions, field, layer) {
  return exceptions.some((entry) => entry.field === field && entry.layer === layer);
}

function checkInventory(paths = defaultPaths) {
  const errors = [];
  const exceptions = loadExceptions(paths.exceptions, errors);

  const requestDto = parseJsonDto(paths.requestDto, errors);
  const contractFields = requestDto.properties.map((property) => property.jsonName);
  const contractNullable = requestDto.properties
    .filter((property) => property.nullable)
    .map((property) => property.jsonName);
  const contractRequired = requestDto.properties
    .filter((property) => property.required)
    .map((property) => property.jsonName);

  if (!existsSync(resolvePath(paths.openApi))) {
    errors.push(`OpenAPI file is missing: ${paths.openApi}.`);
  }

  const openapi = existsSync(resolvePath(paths.openApi)) ? read(paths.openApi) : '';
  const requestSchema = extractSchemaBlock(openapi, paths.openApiSchemaName, errors);
  const openApiProperties = parseProperties(requestSchema);
  const openApiFields = [...openApiProperties.keys()];
  const openApiNullable = [...openApiProperties.values()]
    .filter((property) => property.nullable)
    .map((property) => property.name);
  const openApiRequired = parseRequired(requestSchema);

  assertSameSet(errors, 'OpenAPI vs Contracts request fields', openApiFields, contractFields);
  assertSameSet(
    errors,
    'OpenAPI vs Contracts required request fields',
    openApiRequired,
    contractRequired,
  );
  assertSameSet(
    errors,
    'OpenAPI vs Contracts nullable request fields',
    openApiNullable,
    contractNullable,
  );

  const includedHashFields = parseStringArray(paths.payloadHashContract, 'IncludedFields', errors);
  const excludedHashFields = parseStringArray(paths.payloadHashContract, 'ExcludedFields', errors);

  assertSameSet(
    errors,
    'payload_hash included+excluded vs Contracts request fields',
    [...includedHashFields, ...excludedHashFields],
    contractFields,
  );

  for (const field of includedHashFields) {
    if (excludedHashFields.includes(field)) {
      errors.push(`payload_hash field '${field}' is both included and excluded.`);
    }
  }

  const pythonBuilder = parsePythonBuilderInventory(paths.pythonBuilder, errors);
  const tsBuilder = parseTypeScriptBuilderInventory(paths.tsBuilder, errors);
  const pythonValidationFields = parsePythonValidationFields(paths.pythonValidation, errors);
  const tsValidationFields = parseTypeScriptValidationFields(paths.tsValidation, errors);
  const pythonIncluded = parsePythonIncludedFields(paths.pythonPayloadHash, errors);
  const tsIncluded = parseTypeScriptIncludedFields(paths.tsPayloadHash, errors);

  const layers = [
    { name: 'python-builder', fields: pythonBuilder.fields },
    { name: 'typescript-builder', fields: tsBuilder.fields },
    { name: 'python-validation', fields: pythonValidationFields },
    { name: 'typescript-validation', fields: tsValidationFields },
  ];

  for (const layer of layers) {
    const missing = uniqueSorted(
      contractFields.filter(
        (field) => !layer.fields.includes(field) && !exceptionCovers(exceptions, field, layer.name),
      ),
    );
    const extra = uniqueSorted(
      layer.fields.filter(
        (field) => !contractFields.includes(field) && !exceptionCovers(exceptions, field, layer.name),
      ),
    );

    if (missing.length > 0 || extra.length > 0) {
      errors.push(
        `${layer.name} fields drifted vs Contracts. `
        + `expected=[${uniqueSorted(contractFields).join(', ')}] `
        + `actual=[${uniqueSorted(layer.fields).join(', ')}] `
        + `(${formatSetDiff(contractFields, layer.fields)})`,
      );
    }
  }

  assertSameSet(
    errors,
    'Python builder nullable fields vs Contracts nullable fields',
    pythonBuilder.nullableFields,
    contractNullable,
  );
  assertSameSet(
    errors,
    'Python builder omit-null fields vs Contracts nullable fields',
    pythonBuilder.omitNullFields,
    contractNullable,
  );
  assertSameSet(
    errors,
    'TypeScript builder nullable (explicitNulls) fields vs Contracts nullable fields',
    tsBuilder.nullableFields,
    contractNullable,
  );
  assertSameSet(
    errors,
    'TypeScript builder omit-null fields vs Contracts nullable fields',
    tsBuilder.omitNullFields,
    contractNullable,
  );

  assertSameSet(
    errors,
    'Python SDK INCLUDED_FIELDS vs Contracts IncludedFields',
    pythonIncluded,
    includedHashFields,
  );
  assertSameSet(
    errors,
    'TypeScript SDK INCLUDED_FIELDS vs Contracts IncludedFields',
    tsIncluded,
    includedHashFields,
  );

  return errors;
}

function writeFixtureTree(baseDir, files) {
  for (const [relativePath, contents] of Object.entries(files)) {
    const absolutePath = path.join(baseDir, relativePath);
    mkdirSync(path.dirname(absolutePath), { recursive: true });
    writeFileSync(absolutePath, contents, 'utf8');
  }
}

function baselineFixtureFiles() {
  return {
    'MailRequestCreateRequest.cs': `using System.Text.Json.Serialization;

namespace Fixture;

public sealed record MailRequestCreateRequest
{
    [JsonPropertyName("tenant_id")]
    public required Guid TenantId { get; init; }

    [JsonPropertyName("source_service")]
    public required string SourceService { get; init; }

    [JsonPropertyName("mail_request_id")]
    public required Guid MailRequestId { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    [JsonPropertyName("to")]
    public required IReadOnlyList<object> To { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("html_body")]
    public string? HtmlBody { get; init; }

    [JsonPropertyName("text_body")]
    public string? TextBody { get; init; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("scheduled_at")]
    public DateTimeOffset? ScheduledAt { get; init; }

    [JsonPropertyName("payload_hash")]
    public required string PayloadHash { get; init; }
}
`,
    'openapi.yaml': `components:
  schemas:
    MailRequestCreateRequest:
      type: object
      required:
        - tenant_id
        - source_service
        - mail_request_id
        - purpose
        - to
        - subject
        - payload_hash
      properties:
        tenant_id:
          type: string
        source_service:
          type: string
        mail_request_id:
          type: string
        purpose:
          type: string
        to:
          type: array
        subject:
          type: string
        html_body:
          type: [string, "null"]
        text_body:
          type: [string, "null"]
        reply_to:
          type: [string, "null"]
        metadata:
          type: [object, "null"]
        scheduled_at:
          type: [string, "null"]
        payload_hash:
          type: string
`,
    'MailPayloadHashContract.cs': `namespace Fixture;

public static class MailPayloadHashContract
{
    public static readonly string[] IncludedFields =
    [
        "source_service",
        "purpose",
        "to",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
    ];

    public static readonly string[] ExcludedFields =
    [
        "tenant_id",
        "mail_request_id",
        "payload_hash",
        "scheduled_at",
    ];
}
`,
    'builder.py': `class MailRequestBuilder:
    def __init__(self):
        self._fields = {}
        self._explicit_nulls = set()

    def tenant_id(self, value: str):
        self._fields["tenant_id"] = value
        return self

    def source_service(self, value: str):
        self._fields["source_service"] = value
        return self

    def mail_request_id(self, value: str):
        self._fields["mail_request_id"] = value
        return self

    def purpose(self, value: str):
        self._fields["purpose"] = value
        return self

    def to(self, value):
        self._fields["to"] = value
        return self

    def subject(self, value: str):
        self._fields["subject"] = value
        return self

    def html_body(self, value: str | None):
        self._fields["html_body"] = value
        if value is None:
            self._explicit_nulls.add("html_body")
        else:
            self._explicit_nulls.discard("html_body")
        return self

    def text_body(self, value: str | None):
        self._fields["text_body"] = value
        if value is None:
            self._explicit_nulls.add("text_body")
        else:
            self._explicit_nulls.discard("text_body")
        return self

    def reply_to(self, value: str | None):
        self._fields["reply_to"] = value
        if value is None:
            self._explicit_nulls.add("reply_to")
        else:
            self._explicit_nulls.discard("reply_to")
        return self

    def metadata(self, value: dict | None):
        self._fields["metadata"] = value
        if value is None:
            self._explicit_nulls.add("metadata")
        else:
            self._explicit_nulls.discard("metadata")
        return self

    def scheduled_at(self, value: str | None):
        self._fields["scheduled_at"] = value
        if value is None:
            self._explicit_nulls.add("scheduled_at")
        else:
            self._explicit_nulls.discard("scheduled_at")
        return self

    def build(self):
        draft = dict(self._fields)
        for key in ("html_body", "text_body", "reply_to", "metadata", "scheduled_at"):
            if draft.get(key) is None:
                draft.pop(key, None)
        draft["payload_hash"] = "x"
        return draft
`,
    'validation.py': `MAIL_REQUEST_JSON_FIELDS = frozenset(
    {
        "tenant_id",
        "source_service",
        "mail_request_id",
        "purpose",
        "to",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
        "scheduled_at",
        "payload_hash",
    }
)
`,
    'payload_hash.py': `INCLUDED_FIELDS = frozenset(
    [
        "source_service",
        "purpose",
        "to",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
    ]
)
`,
    'builder.mjs': `export class MailRequestBuilder {
  constructor() {
    this.#fields = {};
    this.#explicitNulls = new Set();
  }

  #fields;
  #explicitNulls;

  tenantId(value) { this.#fields.tenant_id = value; return this; }
  sourceService(value) { this.#fields.source_service = value; return this; }
  mailRequestId(value) { this.#fields.mail_request_id = value; return this; }
  purpose(value) { this.#fields.purpose = value; return this; }
  to(value) { this.#fields.to = value; return this; }
  subject(value) { this.#fields.subject = value; return this; }
  htmlBody(value) {
    this.#fields.html_body = value;
    if (value === null) this.#explicitNulls.add('html_body');
    else this.#explicitNulls.delete('html_body');
    return this;
  }
  textBody(value) {
    this.#fields.text_body = value;
    if (value === null) this.#explicitNulls.add('text_body');
    else this.#explicitNulls.delete('text_body');
    return this;
  }
  replyTo(value) {
    this.#fields.reply_to = value;
    if (value === null) this.#explicitNulls.add('reply_to');
    else this.#explicitNulls.delete('reply_to');
    return this;
  }
  metadata(value) {
    this.#fields.metadata = value;
    if (value === null) this.#explicitNulls.add('metadata');
    else this.#explicitNulls.delete('metadata');
    return this;
  }
  scheduledAt(value) {
    this.#fields.scheduled_at = value;
    if (value === null) this.#explicitNulls.add('scheduled_at');
    else this.#explicitNulls.delete('scheduled_at');
    return this;
  }
  build() {
    const draft = { ...this.#fields };
    for (const key of ['html_body', 'text_body', 'reply_to', 'metadata', 'scheduled_at']) {
      if (draft[key] === undefined) delete draft[key];
    }
    draft.payload_hash = 'x';
    return draft;
  }
}
`,
    'validation.mjs': `export const MAIL_REQUEST_JSON_FIELDS = new Set([
  'tenant_id',
  'source_service',
  'mail_request_id',
  'purpose',
  'to',
  'subject',
  'html_body',
  'text_body',
  'reply_to',
  'metadata',
  'scheduled_at',
  'payload_hash',
]);
`,
    'payload-hash.mjs': `export const INCLUDED_FIELDS = new Set([
  'source_service',
  'purpose',
  'to',
  'subject',
  'html_body',
  'text_body',
  'reply_to',
  'metadata',
]);
`,
    'exceptions.json': `{
  "version": 1,
  "exceptions": []
}
`,
  };
}

function pathsForFixtureDir(fixtureDir) {
  return {
    requestDto: path.join(fixtureDir, 'MailRequestCreateRequest.cs'),
    openApi: path.join(fixtureDir, 'openapi.yaml'),
    openApiSchemaName: 'MailRequestCreateRequest',
    payloadHashContract: path.join(fixtureDir, 'MailPayloadHashContract.cs'),
    pythonBuilder: path.join(fixtureDir, 'builder.py'),
    pythonValidation: path.join(fixtureDir, 'validation.py'),
    pythonPayloadHash: path.join(fixtureDir, 'payload_hash.py'),
    tsBuilder: path.join(fixtureDir, 'builder.mjs'),
    tsValidation: path.join(fixtureDir, 'validation.mjs'),
    tsPayloadHash: path.join(fixtureDir, 'payload-hash.mjs'),
    exceptions: path.join(fixtureDir, 'exceptions.json'),
  };
}

function mutateBaseline(files, mutator) {
  const copy = { ...files };
  mutator(copy);
  return copy;
}

function runSelfTest() {
  const failures = [];
  const tempRoot = mkdtempSync(path.join(tmpdir(), 'amane-field-inventory-'));

  try {
    const cases = [
      {
        name: 'baseline-pass',
        expectPass: true,
        files: baselineFixtureFiles(),
      },
      {
        name: 'missing-sdk-field',
        expectPass: false,
        needle: 'scheduled_at',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['builder.py'] = files['builder.py']
            .replace(/def scheduled_at[\s\S]*?return self\n\n/, '')
            .replace(', "scheduled_at"', '');
          files['validation.py'] = files['validation.py'].replace(/\n\s*"scheduled_at",/, '');
          files['builder.mjs'] = files['builder.mjs']
            .replace(/scheduledAt\(value\)[\s\S]*?return this;\n  }\n/, '')
            .replace(", 'scheduled_at'", '');
          files['validation.mjs'] = files['validation.mjs'].replace(/\n\s*'scheduled_at',/, '');
        }),
      },
      {
        name: 'sdk-only-extra-field',
        expectPass: false,
        needle: 'extra=[legacy_flag]',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['builder.py'] += `

    def legacy_flag(self, value: str):
        self._fields["legacy_flag"] = value
        return self
`;
          files['validation.py'] = files['validation.py'].replace(
            '"payload_hash",',
            '"payload_hash",\n        "legacy_flag",',
          );
          files['builder.mjs'] = files['builder.mjs'].replace(
            'build() {',
            `legacyFlag(value) { this.#fields.legacy_flag = value; return this; }
  build() {`,
          );
          files['validation.mjs'] = files['validation.mjs'].replace(
            "'payload_hash',",
            "'payload_hash',\n  'legacy_flag',",
          );
        }),
      },
      {
        name: 'field-typo',
        expectPass: false,
        needle: 'schduled_at',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['builder.py'] = files['builder.py'].replaceAll('scheduled_at', 'schduled_at');
          files['validation.py'] = files['validation.py'].replace('scheduled_at', 'schduled_at');
        }),
      },
      {
        name: 'hash-classification-miss',
        expectPass: false,
        needle: 'payload_hash included+excluded',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['MailPayloadHashContract.cs'] = files['MailPayloadHashContract.cs']
            .replace('        "scheduled_at",\n', '');
        }),
      },
      {
        name: 'sdk-one-side-only',
        expectPass: false,
        needle: 'typescript-builder fields drifted',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['builder.mjs'] = files['builder.mjs']
            .replace(/scheduledAt\(value\)[\s\S]*?return this;\n  }\n/, '')
            .replace(", 'scheduled_at'", '');
          files['validation.mjs'] = files['validation.mjs'].replace(/\n\s*'scheduled_at',/, '');
        }),
      },
      {
        name: 'nullable-mismatch',
        expectPass: false,
        needle: 'nullable',
        files: mutateBaseline(baselineFixtureFiles(), (files) => {
          files['builder.py'] = files['builder.py']
            .replace(/def scheduled_at[\s\S]*?return self\n\n/, `def scheduled_at(self, value: str | None):
        self._fields["scheduled_at"] = value
        return self

`)
            .replace(', "scheduled_at"', '');
        }),
      },
    ];

    for (const testCase of cases) {
      const fixtureDir = path.join(tempRoot, testCase.name);
      writeFixtureTree(fixtureDir, testCase.files);
      const errors = checkInventory(pathsForFixtureDir(fixtureDir));

      if (testCase.expectPass) {
        if (errors.length > 0) {
          failures.push(
            `${testCase.name}: expected pass, got: ${errors.join('; ')}`,
          );
        }
        continue;
      }

      if (errors.length === 0) {
        failures.push(`${testCase.name}: expected failure`);
        continue;
      }

      if (!errors.some((error) => error.includes(testCase.needle))) {
        failures.push(
          `${testCase.name}: expected needle '${testCase.needle}', got: ${errors.join('; ')}`,
        );
      }
    }
  } finally {
    rmSync(tempRoot, { recursive: true, force: true });
  }

  if (failures.length > 0) {
    console.error('Mail request field inventory self-test failed:');
    for (const failure of failures) {
      console.error(`- ${failure}`);
    }
    process.exit(1);
  }

  console.log('Mail request field inventory self-test passed.');
}

function main() {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === '--self-test') {
    runSelfTest();
    return;
  }

  if (args.length > 0) {
    console.error('Usage: node scripts/check-mail-request-field-inventory.mjs [--self-test]');
    process.exit(2);
  }

  const errors = checkInventory(defaultPaths);
  if (errors.length > 0) {
    console.error('Mail request field inventory check failed:');
    for (const error of errors) {
      console.error(`- ${error}`);
    }
    process.exit(1);
  }

  console.log(
    'Mail request field inventory check passed: Contracts, OpenAPI, '
    + 'payload_hash classification, and Python/TypeScript SDK field sets are in sync.',
  );
}

main();
