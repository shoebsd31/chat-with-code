# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for security problems.

Report them privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability**. Include what you found, how to reproduce it, and what an attacker could
do with it. You'll get a reply within a few days, and a fix or a plan before anything is disclosed.

## What's in scope

- The backend (`src/backend`): authentication of extension requests, access checks against
  Azure DevOps permissions, stored keys, the push-notification endpoint.
- The Azure DevOps extension (`src/extension`).
- The deployment templates and workflows (`infra/`, `.github/workflows/`).

Problems in your own deployment's configuration (for example a leaked API key) are out of scope;
rotate the key in Key Vault or your `.env` and redeploy.

## Supported versions

Only the latest release on `main` gets security fixes.
