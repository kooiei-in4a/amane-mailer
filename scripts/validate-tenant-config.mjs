#!/usr/bin/env node

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..');
const defaultSchemaPath = resolve(repoRoot, 'config/mailer/tenants.schema.json');
const tenantConfigPath = process.argv[2];

if (!tenantConfigPath || process.argv.includes('--help') || process.argv.includes('-h')) {
  console.error('Usage: scripts/validate-tenant-config.sh <tenant-json-path>');
  console.error('');
  console.error('Validates tenant JSON shape and the current environment without printing secret values.');
  process.exit(tenantConfigPath ? 0 : 2);
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    throw new Error(`Could not read ${label} JSON at ${path}: ${error.message}`);
  }
}

const schema = readJson(defaultSchemaPath, 'tenant schema');
const config = readJson(tenantConfigPath, 'tenant config');
const errors = [];
const notes = [];

function fail(message) {
  errors.push(message);
}

function pathJoin(path, segment) {
  return segment.startsWith('[') ? `${path}${segment}` : `${path}.${segment}`;
}

function resolveRef(ref) {
  if (!ref.startsWith('#/')) {
    throw new Error(`Unsupported schema ref: ${ref}`);
  }

  let current = schema;
  for (const part of ref.slice(2).split('/')) {
    current = current[part.replaceAll('~1', '/').replaceAll('~0', '~')];
    if (current === undefined) {
      throw new Error(`Unresolved schema ref: ${ref}`);
    }
  }

  return current;
}

function jsonTypeOf(value) {
  if (value === null) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return 'array';
  }
  if (Number.isInteger(value)) {
    return 'integer';
  }
  return typeof value;
}

function validateType(expected, value, path) {
  const actual = jsonTypeOf(value);
  if (expected === 'integer') {
    if (actual !== 'integer') {
      fail(`${path} must be an integer.`);
    }
    return;
  }

  if (actual !== expected) {
    fail(`${path} must be ${expected}.`);
  }
}

function validateFormat(format, value, path) {
  if (typeof value !== 'string') {
    return;
  }

  if (
    format === 'uuid' &&
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  ) {
    fail(`${path} must be a UUID.`);
  }

  if (
    format === 'email' &&
    !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)
  ) {
    fail(`${path} must be an email address.`);
  }
}

function stableJson(value) {
  if (value === null || typeof value !== 'object') {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableJson(item)).join(',')}]`;
  }

  return `{${Object.keys(value)
    .sort()
    .map((key) => `${JSON.stringify(key)}:${stableJson(value[key])}`)
    .join(',')}}`;
}

function validateSchema(schemaNode, value, path) {
  if (schemaNode.$ref) {
    validateSchema(resolveRef(schemaNode.$ref), value, path);
    return;
  }

  if (schemaNode.type) {
    validateType(schemaNode.type, value, path);
  }

  if (Object.hasOwn(schemaNode, 'const') && value !== schemaNode.const) {
    fail(`${path} must be ${JSON.stringify(schemaNode.const)}.`);
  }

  if (schemaNode.enum && !schemaNode.enum.includes(value)) {
    fail(`${path} must be one of: ${schemaNode.enum.join(', ')}.`);
  }

  if (schemaNode.type === 'object' && value !== null && !Array.isArray(value) && typeof value === 'object') {
    const properties = schemaNode.properties ?? {};
    for (const required of schemaNode.required ?? []) {
      if (!Object.hasOwn(value, required)) {
        fail(`${pathJoin(path, required)} is required.`);
      }
    }

    if (schemaNode.additionalProperties === false) {
      for (const key of Object.keys(value)) {
        if (!Object.hasOwn(properties, key)) {
          fail(`${pathJoin(path, key)} is not allowed by the tenant schema.`);
        }
      }
    }

    for (const [key, childSchema] of Object.entries(properties)) {
      if (Object.hasOwn(value, key)) {
        validateSchema(childSchema, value[key], pathJoin(path, key));
      }
    }
  }

  if (schemaNode.type === 'array' && Array.isArray(value)) {
    if (schemaNode.minItems !== undefined && value.length < schemaNode.minItems) {
      fail(`${path} must contain at least ${schemaNode.minItems} item(s).`);
    }

    if (schemaNode.uniqueItems) {
      const seen = new Set();
      for (const [index, item] of value.entries()) {
        const key = stableJson(item);
        if (seen.has(key)) {
          fail(`${path}[${index}] duplicates an earlier item.`);
        }
        seen.add(key);
      }
    }

    if (schemaNode.items) {
      for (const [index, item] of value.entries()) {
        validateSchema(schemaNode.items, item, `${path}[${index}]`);
      }
    }
  }

  if (schemaNode.type === 'string' && typeof value === 'string') {
    if (schemaNode.minLength !== undefined && value.length < schemaNode.minLength) {
      fail(`${path} must not be empty.`);
    }
    if (schemaNode.pattern && !new RegExp(schemaNode.pattern).test(value)) {
      fail(`${path} does not match the required pattern.`);
    }
    if (schemaNode.format) {
      validateFormat(schemaNode.format, value, path);
    }
  }

  if (schemaNode.type === 'integer' && Number.isInteger(value)) {
    if (schemaNode.minimum !== undefined && value < schemaNode.minimum) {
      fail(`${path} must be >= ${schemaNode.minimum}.`);
    }
    if (schemaNode.maximum !== undefined && value > schemaNode.maximum) {
      fail(`${path} must be <= ${schemaNode.maximum}.`);
    }
  }
}

function tenantLabel(tenant, index) {
  if (tenant && typeof tenant.name === 'string' && tenant.name.length > 0) {
    return `tenant '${tenant.name}'`;
  }

  return `tenant at index ${index}`;
}

function readEnv(name) {
  return process.env[name];
}

function readFirstEnv(...names) {
  for (const name of names) {
    const value = readEnv(name);
    if (value !== undefined) {
      return value;
    }
  }

  return undefined;
}

function readProviderOverride() {
  return (readFirstEnv('MAILER_PROVIDER', 'Mailer__Provider', 'Mailer:Provider') ?? '').trim();
}

function looksLikePlaceholder(value) {
  const normalized = value.trim().toLowerCase();
  if (normalized.length === 0) {
    return true;
  }

  return (
    /^<.*>$/.test(normalized) ||
    normalized.startsWith('replace-with') ||
    normalized.includes('replace_with') ||
    normalized.includes('placeholder') ||
    normalized.includes('change-me') ||
    normalized.includes('changeme') ||
    normalized.includes('todo') ||
    normalized.includes('your-token') ||
    normalized.includes('your-secret') ||
    normalized.includes('dummy-token') ||
    normalized.includes('example-token') ||
    normalized.includes('secret-here')
  );
}

function validateEnvironment() {
  if (!Array.isArray(config.tenants)) {
    return;
  }

  const tenantIds = new Map();
  const providerOverride = readProviderOverride();
  const providerCounts = { acs: 0, mailpit: 0 };
  let hasMailpitTenant = false;

  if (providerOverride && !['acs', 'mailpit'].includes(providerOverride)) {
    fail('MAILER_PROVIDER / Mailer__Provider / Mailer:Provider must be either acs or mailpit when set.');
  }

  for (const [index, tenant] of config.tenants.entries()) {
    const label = tenantLabel(tenant, index);

    if (tenant && typeof tenant.tenant_id === 'string') {
      const previous = tenantIds.get(tenant.tenant_id);
      if (previous !== undefined) {
        fail(`${label} duplicates tenant_id from tenant at index ${previous}.`);
      } else {
        tenantIds.set(tenant.tenant_id, index);
      }
    }

    if (tenant && Array.isArray(tenant.source_services)) {
      if (tenant.source_services.length === 0) {
        fail(`${label} must list at least one source_service.`);
      }

      const seenSourceServices = new Set();
      for (const sourceService of tenant.source_services) {
        if (seenSourceServices.has(sourceService)) {
          fail(`${label} has duplicate source_service '${sourceService}'.`);
        }
        seenSourceServices.add(sourceService);
      }
    }

    if (!tenant || typeof tenant !== 'object' || Array.isArray(tenant)) {
      continue;
    }

    if (tenant && typeof tenant.token_env === 'string') {
      const tokenValue = readEnv(tenant.token_env);
      if (tokenValue === undefined) {
        fail(`${label} token_env ${tenant.token_env} is not set in the current environment.`);
      } else if (tokenValue.length === 0) {
        fail(`${label} token_env ${tenant.token_env} is set but empty.`);
      } else if (looksLikePlaceholder(tokenValue)) {
        fail(`${label} token_env ${tenant.token_env} appears to contain a placeholder value.`);
      }
    }

    const effectiveProvider = providerOverride || tenant.provider;
    if (effectiveProvider === 'acs' || effectiveProvider === 'mailpit') {
      providerCounts[effectiveProvider] += 1;
    }

    if (effectiveProvider === 'acs' && tenant.live_sending === true) {
      const acsConnectionString = readEnv('ACS_CONNECTION_STRING');
      if (acsConnectionString === undefined || acsConnectionString.length === 0) {
        fail(`${label} uses effective provider acs with live_sending=true, but ACS_CONNECTION_STRING is not set.`);
      } else if (looksLikePlaceholder(acsConnectionString)) {
        fail(`${label} uses effective provider acs with live_sending=true, but ACS_CONNECTION_STRING appears to contain a placeholder value.`);
      }
    }

    if (effectiveProvider === 'mailpit') {
      hasMailpitTenant = true;
    }
  }

  if (hasMailpitTenant) {
    const mailpitPort = readFirstEnv(
      'MAILPIT_SMTP_PORT',
      'Mailer__Mailpit__SmtpPort',
      'Mailer:Mailpit:SmtpPort',
    );
    if (mailpitPort !== undefined && !/^[1-9][0-9]*$/.test(mailpitPort)) {
      fail('MAILPIT_SMTP_PORT / Mailer__Mailpit__SmtpPort / Mailer:Mailpit:SmtpPort must be a positive integer when set.');
    }

    notes.push('Mailpit SMTP settings use MAILPIT_SMTP_HOST / MAILPIT_SMTP_PORT or .NET double-underscore aliases when set; otherwise runtime defaults apply.');
  }

  notes.push(`Effective provider count: acs=${providerCounts.acs}, mailpit=${providerCounts.mailpit}.`);
}

try {
  validateSchema(schema, config, '$');
  validateEnvironment();
} catch (error) {
  fail(error.message);
}

if (errors.length > 0) {
  console.error(`Tenant config preflight failed for ${tenantConfigPath}:`);
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(`Tenant config preflight passed for ${tenantConfigPath}.`);
console.log(`Schema: ${defaultSchemaPath}.`);
console.log(`Tenants: ${config.tenants.length}.`);
for (const note of notes) {
  console.log(note);
}
