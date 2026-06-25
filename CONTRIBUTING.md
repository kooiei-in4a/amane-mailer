# Contributing to Amane Mailer

Thank you for your interest in contributing.

## Getting started

1. Fork the repository and create a branch from `main`.
2. Install prerequisites: .NET SDK (see `global.json`), Docker Desktop.
3. Verify the build and tests pass locally:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

4. Open a pull request describing your change and the motivation behind it.

## Reporting issues

Use GitHub Issues. For security vulnerabilities, see [Security](#security) below.

## Pull requests

- Keep PRs focused on a single concern.
- Include or update tests for behaviour changes.
- Update documentation when adding or changing features.

## Security

See [SECURITY.md](SECURITY.md) for vulnerability reporting and
secret-handling guidelines.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE) that covers this project.
