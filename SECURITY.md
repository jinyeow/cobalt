# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately via GitHub's
[Report a vulnerability](https://github.com/jinyeow/cobalt/security/advisories/new)
flow rather than opening a public issue. You'll get an acknowledgement and, once
a fix is available, a coordinated disclosure.

## Scope notes

- **Credentials.** cobalt authenticates with Entra ID via `Azure.Identity` and
  never stores a password or PAT. Tokens live in the MSAL cache (encrypted where
  the OS provides a keyring; a `0600` file otherwise) and the non-secret
  `AuthenticationRecord` sits next to the config file. Report anything that would
  leak or mishandle a token.
- **Config.** `~/.config/cobalt/config.toml` holds only an organization URL and
  project name — no secrets.
- **Automated checks.** CodeQL (security-and-quality) runs on every push and PR,
  and Dependabot watches the NuGet and GitHub Actions dependencies.
