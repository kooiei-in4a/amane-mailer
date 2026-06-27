#!/usr/bin/env node

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const errors = [];

function read(relativePath) {
  return readFileSync(path.join(root, relativePath), 'utf8');
}

function fail(message) {
  errors.push(message);
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

function assertSameSet(label, actual, expected) {
  if (sameSet(actual, expected)) {
    return;
  }

  fail(
    `${label} drifted. expected=[${uniqueSorted(expected).join(', ')}] `
    + `actual=[${uniqueSorted(actual).join(', ')}]`,
  );
}

function assertContains(source, needle, label) {
  if (!source.includes(needle)) {
    fail(`${label} is missing '${needle}'.`);
  }
}

function assertMatches(source, pattern, label, description) {
  if (!pattern.test(source)) {
    fail(`${label} is missing '${description}'.`);
  }
}

function parseJsonDto(relativePath) {
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
    fail(`No JSON properties found in ${relativePath}.`);
  }

  return {
    relativePath,
    source,
    properties,
    rejectsUnknownMembers: source.includes(
      '[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]',
    ),
  };
}

function parseStringConstants(relativePath, typeName) {
  const source = read(relativePath);
  const constants = [...source.matchAll(/public const string\s+([A-Za-z_]\w*)\s*=\s*"([^"]+)";/g)]
    .map((match) => ({ name: match[1], value: match[2] }));

  if (constants.length === 0) {
    fail(`No string constants found for ${typeName} in ${relativePath}.`);
  }

  return constants;
}

function parseStringArray(relativePath, propertyName) {
  const source = read(relativePath);
  const pattern = new RegExp(
    `public\\s+static\\s+readonly\\s+string\\[\\]\\s+${propertyName}\\s*=\\s*\\[(.*?)\\];`,
    's',
  );
  const match = source.match(pattern);
  if (!match) {
    fail(`Could not find ${propertyName} in ${relativePath}.`);
    return [];
  }

  return [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
}

function extractSchemaBlock(source, schemaName) {
  const lines = source.split(/\r?\n/);
  const start = lines.findIndex((line) => line === `    ${schemaName}:`);
  if (start < 0) {
    fail(`OpenAPI schema ${schemaName} is missing.`);
    return '';
  }

  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (/^ {4}[A-Za-z0-9_.-]+:\s*$/.test(lines[index])) {
      end = index;
      break;
    }
  }

  return lines.slice(start + 1, end).join('\n');
}

function parseRequired(schemaBlock) {
  const inline = schemaBlock.match(/^ {6}required:\s*\[([^\]]*)\]\s*$/m);
  if (inline) {
    return inline[1]
      .split(',')
      .map((value) => value.trim())
      .filter(Boolean);
  }

  const lines = schemaBlock.split(/\r?\n/);
  const start = lines.findIndex((line) => /^ {6}required:\s*$/.test(line));
  if (start < 0) {
    return [];
  }

  const required = [];
  for (let index = start + 1; index < lines.length; index += 1) {
    const match = lines[index].match(/^ {8}-\s+([A-Za-z0-9_]+)\s*$/);
    if (!match) {
      if (/^\s*$/.test(lines[index])) {
        continue;
      }

      break;
    }

    required.push(match[1]);
  }

  return required;
}

function extractPropertiesBlock(schemaBlock) {
  const lines = schemaBlock.split(/\r?\n/);
  const start = lines.findIndex((line) => /^ {6}properties:\s*$/.test(line));
  if (start < 0) {
    return [];
  }

  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (/^ {0,6}\S/.test(lines[index])) {
      end = index;
      break;
    }
  }

  return lines.slice(start + 1, end);
}

function parseProperties(schemaBlock) {
  const lines = extractPropertiesBlock(schemaBlock);
  const properties = new Map();

  for (let index = 0; index < lines.length; index += 1) {
    const match = lines[index].match(/^ {8}([A-Za-z0-9_]+):\s*$/);
    if (!match) {
      continue;
    }

    const name = match[1];
    let end = lines.length;
    for (let next = index + 1; next < lines.length; next += 1) {
      if (/^ {8}[A-Za-z0-9_]+:\s*$/.test(lines[next])) {
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

function parseEnum(propertyBlock) {
  const inline = propertyBlock.match(/^ {10}enum:\s*\[([^\]]*)\]\s*$/m);
  if (inline) {
    return inline[1]
      .split(',')
      .map((value) => value.trim())
      .filter(Boolean);
  }

  const lines = propertyBlock.split(/\r?\n/);
  const start = lines.findIndex((line) => /^ {10}enum:\s*$/.test(line));
  if (start < 0) {
    return [];
  }

  const values = [];
  for (let index = start + 1; index < lines.length; index += 1) {
    const match = lines[index].match(/^ {12}-\s+(.+?)\s*$/);
    if (!match) {
      if (/^\s*$/.test(lines[index])) {
        continue;
      }

      break;
    }

    values.push(match[1].replace(/^["']|["']$/g, ''));
  }

  return values;
}

function isOpenApiNullable(propertyBlock) {
  return (
    /^ {10}nullable:\s*true\s*$/m.test(propertyBlock)
    || /^ {10}type:\s*\[[^\]]*\bnull\b[^\]]*\]\s*$/m.test(propertyBlock)
  );
}

function parseOpenApiSchema(openapi, schemaName) {
  const block = extractSchemaBlock(openapi, schemaName);
  const properties = parseProperties(block);

  return {
    name: schemaName,
    block,
    required: parseRequired(block),
    properties,
    additionalPropertiesFalse: /^ {6}additionalProperties:\s*false\s*$/m.test(block),
  };
}

function compareDtoToOpenApiSchema(dtoName, dto, schema, options = {}) {
  const contractProperties = dto.properties.map((property) => property.jsonName);
  const openApiProperties = [...schema.properties.keys()];
  assertSameSet(`${dtoName} properties`, openApiProperties, contractProperties);

  const requiredProperties = dto.properties
    .filter((property) => property.required)
    .map((property) => property.jsonName);
  assertSameSet(`${dtoName} required properties`, schema.required, requiredProperties);

  const nullableProperties = dto.properties
    .filter((property) => property.nullable)
    .map((property) => property.jsonName);
  const openApiNullableProperties = [...schema.properties.values()]
    .filter((property) => property.nullable)
    .map((property) => property.name);
  assertSameSet(`${dtoName} nullable properties`, openApiNullableProperties, nullableProperties);

  if (options.expectClosedSchema && !schema.additionalPropertiesFalse) {
    fail(`${schema.name} must set additionalProperties: false to match DTO strictness.`);
  }
}

const openapi = read('docs/api/openapi.yaml');
const serviceSpecs = `${read('docs/service-spec.md')}\n${read('docs/service-spec.en.md')}`;
const contractsReadme = read('src/Amane.Mailer.Contracts/README.md');

const requestDto = parseJsonDto('src/Amane.Mailer.Contracts/MailRequests/MailRequestCreateRequest.cs');
const responseDto = parseJsonDto('src/Amane.Mailer.Contracts/MailRequests/MailRequestCreateResponse.cs');
const recipientDto = parseJsonDto('src/Amane.Mailer.Contracts/MailRequests/MailRecipientDto.cs');

const requestSchema = parseOpenApiSchema(openapi, 'MailRequestCreateRequest');
const responseSchema = parseOpenApiSchema(openapi, 'MailRequestCreateResponse');
const recipientSchema = parseOpenApiSchema(openapi, 'MailRecipient');
const errorSchema = parseOpenApiSchema(openapi, 'Error');

compareDtoToOpenApiSchema('MailRequestCreateRequest', requestDto, requestSchema, {
  expectClosedSchema: requestDto.rejectsUnknownMembers,
});
compareDtoToOpenApiSchema('MailRecipientDto', recipientDto, recipientSchema, {
  expectClosedSchema: recipientDto.rejectsUnknownMembers,
});
compareDtoToOpenApiSchema('MailRequestCreateResponse', responseDto, responseSchema);

if (!requestDto.rejectsUnknownMembers) {
  fail('MailRequestCreateRequest must reject unknown JSON members.');
}

if (!recipientDto.rejectsUnknownMembers) {
  fail('MailRecipientDto must reject unknown JSON members.');
}

const errorCodes = parseStringConstants(
  'src/Amane.Mailer.Contracts/MailRequests/MailerErrorCodes.cs',
  'MailerErrorCodes',
);
const acceptanceStatuses = parseStringConstants(
  'src/Amane.Mailer.Contracts/MailRequests/MailRequestAcceptanceStatus.cs',
  'MailRequestAcceptanceStatus',
);
const deliveryStatuses = parseStringConstants(
  'src/Amane.Mailer.Contracts/MailRequests/MailRequestStatus.cs',
  'MailRequestStatus',
);

const errorCodeProperty = errorSchema.properties.get('code');
if (!errorCodeProperty) {
  fail('OpenAPI Error.code property is missing.');
} else {
  assertSameSet(
    'MailerErrorCodes vs OpenAPI Error.code enum',
    parseEnum(errorCodeProperty.block),
    errorCodes.map((constant) => constant.value),
  );
}

const statusProperty = responseSchema.properties.get('status');
if (!statusProperty) {
  fail('OpenAPI MailRequestCreateResponse.status property is missing.');
} else {
  assertSameSet(
    'MailRequestAcceptanceStatus vs OpenAPI response status enum',
    parseEnum(statusProperty.block),
    acceptanceStatuses.map((constant) => constant.value),
  );
}

const includedHashFields = parseStringArray(
  'src/Amane.Mailer.Contracts/Security/MailPayloadHashContract.cs',
  'IncludedFields',
);
const excludedHashFields = parseStringArray(
  'src/Amane.Mailer.Contracts/Security/MailPayloadHashContract.cs',
  'ExcludedFields',
);

assertSameSet(
  'payload_hash included+excluded fields vs request DTO properties',
  [...includedHashFields, ...excludedHashFields],
  requestDto.properties.map((property) => property.jsonName),
);

for (const field of includedHashFields) {
  if (excludedHashFields.includes(field)) {
    fail(`payload_hash field '${field}' is both included and excluded.`);
  }
}

const payloadHashProperty = requestSchema.properties.get('payload_hash');
if (!payloadHashProperty) {
  fail('OpenAPI MailRequestCreateRequest.payload_hash property is missing.');
} else {
  for (const field of [...includedHashFields, ...excludedHashFields]) {
    assertContains(payloadHashProperty.block, field, 'OpenAPI payload_hash description');
  }
}

for (const status of deliveryStatuses) {
  const documented =
    serviceSpecs.includes(`status_${status.value}`)
    || serviceSpecs.includes(`\`${status.value}\``)
    || serviceSpecs.includes(`\`${status.name}\``);

  if (!documented) {
    fail(`MailRequestStatus.${status.name} (${status.value}) is not documented in service spec.`);
  }
}

const runtimeJsonContext = read('src/Amane.Mailer/Json/MailerJsonContext.cs');
for (const typeName of ['MailRequestCreateRequest', 'MailRequestCreateResponse', 'MailRecipientDto']) {
  assertContains(
    runtimeJsonContext,
    `[JsonSerializable(typeof(${typeName}))]`,
    'Runtime JSON source-generation context',
  );
}
assertContains(
  runtimeJsonContext,
  'DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull',
  'Runtime JSON source-generation context',
);

const contractsJsonContext = read('src/Amane.Mailer.Contracts/Json/MailerContractsJsonContext.cs');
for (const typeName of ['MailRequestCreateRequest', 'MailRequestCreateResponse', 'MailRecipientDto']) {
  assertContains(
    contractsJsonContext,
    `[JsonSerializable(typeof(${typeName}))]`,
    'Contracts JSON source-generation context',
  );
}
assertContains(
  contractsJsonContext,
  'DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull',
  'Contracts JSON source-generation context',
);

const runtimeContractSources = [
  read('src/Amane.Mailer/Api/MailRequestEndpoints.cs'),
  read('src/Amane.Mailer/Json/MailerJsonResults.cs'),
].join('\n');

assertMatches(
  runtimeContractSources,
  /JsonSerializer\.Deserialize\s*\(\s*requestBody\s*,\s*MailerJsonContext\.Default\.MailRequestCreateRequest\s*\)/s,
  'Runtime request deserialization',
  'JsonSerializer.Deserialize(... MailerJsonContext.Default.MailRequestCreateRequest)',
);
assertMatches(
  runtimeContractSources,
  /JsonDuplicatePropertyDetector\.HasDuplicateProperty\s*\(\s*requestBody\s*\)/s,
  'Runtime duplicate-property rejection',
  'JsonDuplicatePropertyDetector.HasDuplicateProperty(requestBody)',
);
assertMatches(
  runtimeContractSources,
  /MailPayloadHasher\.ComputeDeliveryPayloadSha256Hex\s*\(\s*requestBody\s*\)/s,
  'Runtime payload hash validation',
  'MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(requestBody)',
);
assertMatches(
  runtimeContractSources,
  /MailerJsonResults\.Accepted\s*\(\s*new\s+MailRequestCreateResponse/s,
  'Runtime accepted response serialization',
  'MailerJsonResults.Accepted(new MailRequestCreateResponse)',
);

for (const constant of errorCodes) {
  assertContains(
    runtimeContractSources,
    `MailerErrorCodes.${constant.name}`,
    'Runtime error-code constants',
  );
}

for (const constant of acceptanceStatuses) {
  assertContains(
    runtimeContractSources,
    `MailRequestAcceptanceStatus.${constant.name}`,
    'Runtime acceptance-status constants',
  );
}

const contractStrictnessTests = read('tests/Amane.Mailer.Contracts.Tests/MailRequestDtoStrictnessTests.cs');
for (const needle of [
  'MailRequestCreateRequest_rejects_unknown_property',
  'MailRecipientDto_rejects_unknown_property',
  'MailRequestCreateRequest_accepts_known_properties',
]) {
  assertContains(contractStrictnessTests, needle, 'Contracts JSON strictness tests');
}

const runtimeApiTests = read('tests/Amane.Mailer.Tests/MailRequestApiTests.cs');
for (const needle of [
  'Unknown_top_level_property_returns_400',
  'Unknown_recipient_property_returns_400',
  'Duplicate_top_level_property_returns_400',
  'Duplicate_recipient_property_returns_400',
  'Duplicate_metadata_property_returns_400',
]) {
  assertContains(runtimeApiTests, needle, 'Runtime JSON strictness tests');
}

const payloadHashTests = read('tests/Amane.Mailer.Contracts.Tests/MailPayloadHasherTests.cs');
for (const needle of [
  'Shared_test_vectors_match_canonical_json_and_hash',
  'BuildDeliveryPayloadJson_excludes_routing_envelope_fields',
  'Openapi_example_payload_hash_matches_documented_value',
]) {
  assertContains(payloadHashTests, needle, 'Payload hash contract tests');
}

if (!existsSync(path.join(root, 'tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json'))) {
  fail('payload_hash test vector fixture is missing.');
}

for (const needle of [
  'scripts/check-contract-drift.mjs',
  'payload-hash-vectors.json',
]) {
  assertContains(contractsReadme, needle, 'Contracts README update guidance');
}

if (errors.length > 0) {
  console.error('Contract drift check failed:');
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(
  'Contract drift check passed: Contracts DTOs/constants, runtime JSON behavior, '
  + 'OpenAPI schemas/enums, payload_hash fields, and strictness test coverage are in sync.',
);
