# Contributing

Thanks for helping! Contributions come in as pull requests from forks.

1. **Open an issue first** for anything bigger than a small fix, so we can agree on the approach.
2. **Fork** the repository and create a branch from `main`.
3. **Make the change with tests.** Run them before pushing:
   - Backend: `cd src/backend && dotnet test` (the database tests need Docker)
   - Extension: `cd src/extension && npm ci && npm test && npx tsc --noEmit -p .`
4. **Open a pull request** against `main` describing what changed and why.

`main` is protected: every change goes through a pull request, **Backend CI** and **Extension CI**
must pass, and the maintainer reviews it. Workflows from first-time contributors run after approval.

Never put secrets (keys, tokens, connection strings) in code, tests or workflow files. Deployment
secrets live in Azure Key Vault and GitHub environment variables only.

Security problems: see [SECURITY.md](SECURITY.md) instead of opening an issue.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
