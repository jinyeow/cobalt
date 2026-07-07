# 0003 — Entra ID-only auth via Azure.Identity credential chain

Status: Accepted · Date: 2026-07-04

## Context

Azure DevOps accepts PATs and Entra ID (OAuth) bearer tokens. The user
explicitly rejected PATs (expiry/scope management chores). They already use
`az login` day-to-day.

## Decision

Authenticate exclusively with Entra ID bearer tokens obtained through
`Azure.Identity`, in a chain:

1. `AzureCliCredential` — reuse an existing `az login` session (zero setup,
   and the escape hatch for tenants whose conditional-access policies block
   public-client flows).
2. `InteractiveBrowserCredential` — browser sign-in with a persistent token
   cache (`TokenCachePersistenceOptions`), so interactive login is rare.

Tokens are requested for the Azure DevOps resource scope
`499b84ac-1321-427f-aa17-267ca6975798/.default`. The MSAL cache allows
unencrypted file storage as a fallback (`UnencryptedStorageAllowed = true`)
because Linux boxes without a keyring (headless, some WSL setups) otherwise
cannot persist tokens at all — the same tradeoff the az CLI makes. The
`AuthenticationRecord` (not a secret; account metadata only) is stored next to
the config file so later runs authenticate silently.

## Consequences

No secrets in config files, no PAT rotation. Costs: `Azure.Identity` dependency
weight, cloud-only (fine — ADR 0002 already scopes to Services), and headless
machines must use the az CLI path.
