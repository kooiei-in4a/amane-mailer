## Summary

- TBD

## Validation

- [ ] `dotnet restore Amane.Mailer.slnx --locked-mode`
- [ ] `dotnet build Amane.Mailer.slnx -c Release --no-restore`
- [ ] `dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal`
- [ ] `docker compose -f infra/docker/docker-compose.local.yml config --quiet`

## Security and Privacy

- [ ] No real tenant files, tokens, ACS connection strings, database files, or
      private infrastructure details were added.
- [ ] Any new GitHub Actions are pinned to full-length commit SHAs.

## Implementation Status

- [ ] If this PR changes or touches related feature behavior, `docs/implementation-status.json` was reviewed and updated as needed.
- [ ] ADRs were not updated with a new duplicate "current implementation status" section.

## Notes

- TBD
