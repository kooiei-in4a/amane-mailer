[日本語](ghcr-image-publish.md)

# GHCR image publishing

GitHub Actions will publish the Mailer runtime image to GitHub Container Registry.

Workflow:

- `.github/workflows/publish-image.yml`
- Trigger: manual `workflow_dispatch`

## Image

- `ghcr.io/<github-org>/amane-mailer`

The image name is derived from `${{ github.repository_owner }}` at publish time.

## Tags

- Manual publishes via `workflow_dispatch` produce an immutable `sha-<git-sha>` tag.
- After CI is stable, pushes to `develop` may publish `sha-<git-sha>` and the
  mutable `develop` tag.
- Protected release branches may publish immutable `sha-<git-sha>` tags after
  environment approvals are configured.
- `staging` and `main` should be deployed by immutable `sha-<git-sha>` tags.

## GitHub Actions permissions

The publish job uses:

- `contents: read`
- `packages: write`

No repository secret is needed for pushing images. The workflow uses `GITHUB_TOKEN`.

The workflow builds the Mailer image from `infra/docker/Dockerfile`.

## First publish

1. Run `Publish Amane Mailer Image` from the `main` branch via `workflow_dispatch`.
2. Use the generated immutable tag: `sha-<git-sha>`.
3. Confirm the workflow's image run and config-content checks pass.

The runtime image includes only safe files from `config/mailer`:

- `tenants.example.json`
- `tenants.local-acs.json.example`
- `tenants.schema.json`

Deployment-specific tenant JSON is not baked into the image. Shared deployments
mount a host-owned tenant file with `MAILER_TENANTS_HOST_PATH` and
`MAILER_TENANTS_CONTAINER_PATH` from `infra/deploy/compose.yml`. A tenant JSON
change is therefore a config deploy, not an image rebuild.

## GitHub Environments

The environments below are planned deployment gates:

- `development`: used by pushes to `develop`.
- `staging`: used by pushes to `staging`.
- `production`: used by pushes to `main`.

Configure the `production` environment in GitHub with a required reviewer before
allowing production image publication.

## Deploy host pull access

If GHCR packages are private, the deploy host must authenticate before `docker compose pull`.

Use a read-only personal access token with `read:packages` scope:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```
