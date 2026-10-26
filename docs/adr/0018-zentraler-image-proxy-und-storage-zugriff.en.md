# 0018 — Central Image Proxy and Delegated Storage Access (SAS Broker)

- **Status:** Accepted
- **Date:** 2026-10-25
- **Supplements:** [0017](0017-konfigurierbare-bildspeicher.en.md) (providers, cache, migration), [0016](0016-bilder-in-lernkarten.en.md) (unchanged image format)
- **Related:** [0011](0011-mehrbenutzerarchitektur.en.md), [0015](0015-freigaben-globale-lernmodule.md) (authorization), [0014](0014-sitzungs-und-betriebsmodus-haertung.en.md) (hardening)

## Context

Depending on the storage provider, images reside in private cloud containers. Clients (browsers, MCP) must neither access those containers directly nor see credentials: no account keys, SAS tokens, Graph URLs, or presigned URLs. Meanwhile, the server should access storage with minimal, short-lived permissions and eventually support a separate SAS-issuing service.

## Decision

1. **The only client-facing path is `GET /api/images/{imageId}`.** Sequence: authenticate → authorize via `CardRepository.GetReadableImageAsync` (owned/shared/global card under ADRs 0011/0015; unauthorized records → 404) → look up provider and key internally from metadata (**never supplied by clients**) → cache → storage → serve bytes with the correct `Content-Type`, `X-Content-Type-Options: nosniff`, SHA-256 `ETag`, and `Cache-Control: private`. **Never redirect** to cloud URLs or expose credentials. Card JSON contains only `url = /api/images/{id}` (no provider, storage key, or cloud URL). The UI uses only this URL, with `loading="lazy"` and alternative text/captions. Emergency switch `IMAGE_PROXY_ENABLED=false` disables the proxy (503, `proxy_disabled`).
2. **Separate storage provider and access broker.** `IStorageAccessBroker.GetReadGrantAsync(key)` returns `StorageGrant` (URL, expiration): **read-only, restricted to one object, short-lived**. The broker has no user knowledge; the application performs authorization first. Providers perform writes/deletes/checks with their own server-side identity and read only via the grant.
3. **Azure Blob:**
   - Production: Entra ID (prefer Managed Identity, otherwise client secret) and **user-delegation SAS** (delegation key obtained using “Get User Delegation Key,” held only in memory; requires *Storage Blob Delegator* plus a data role such as *Storage Blob Data Contributor*).
   - SAS parameters: `r` permission only, `sr=b` (**one blob**, never container/account SAS), version 2020-12-06, start −2 min (clock skew), expiration `IMAGE_AZURE_SAS_TTL_SECONDS` (default **60 s**, allowed 10–600), `spr=https`.
   - Development/test (Azurite): `IMAGE_AZUREBLOB_AUTH=sharedkey` → service SAS signed with the account key (same blob-specific structure, `sp=r`). Not for production; Azurite OAuth/user delegation without HTTPS could not be tested and was not verified.
   - Never persist SAS in the database, log it, or return it to clients. If storage rejects a grant (expired/revoked), reset the broker and obtain **exactly one** new grant; then return a controlled error.
4. **S3-compatible:** Presigned GET (SigV4 query signing, only `host` signed, `IMAGE_S3_PRESIGN_TTL_SECONDS` defaults to 60 s) for exactly one object; writes/deletes/checks use signed headers.
5. **SharePoint:** Microsoft Graph with a server token (app-only); the short-lived `downloadUrl` is used in memory for a single fetch and accepted only with HTTPS and a host ending in `*.sharepoint.com|.us|.de|.cn` or `*.files.1drv.com`.
6. **Database/local:** Internal access without a broker.
7. **Future external SAS handler (contract, not implemented):** A separate service implements `IStorageAccessBroker`: input = provider key (32 hex characters), output = `StorageGrant`. Trust boundary: the application trusts the handler only to *issue* grants. It **revalidates** every returned URL (`StorageUrlGuard`) and fetches only configured endpoints/containers. The handler authenticates the application using Managed Identity/mTLS (not user tokens), issues read-only short-lived grants, and audits issuance without logging SAS content.
8. **Safe cloud requests (`SafeHttp`, `StorageUrlGuard`):**
   - Destinations consist only of configured endpoint/container or bucket and server-generated keys; **never** accept host/path from user data. Before using broker URLs, verify exact scheme/host/port and expected path, no userinfo or fragments, permitted query parameters only, and `sp=r`/`sr=b`.
   - HTTPS is mandatory; HTTP is supported only with a development flag (`…_ALLOW_HTTP`).
   - **No redirects** (`AllowAutoRedirect=false`), no proxy. Resolve DNS and check the address **before connecting** to prevent SSRF/DNS rebinding. Block loopback, private, link-local (including metadata endpoint 169.254.169.254), CGNAT, multicast, and IPv6 ULA unless explicitly allowed through `IMAGE_AZUREBLOB_/IMAGE_S3_/IMAGE_SHAREPOINT_ALLOW_PRIVATE_NETWORK=true` (private endpoints/Azurite).
   - Enforce `IMAGE_HTTP_TIMEOUT_SECONDS`, `CancellationToken`, a hard 2 MiB streamed-response limit, and one controlled retry for HTTP 429/503.
   - Map errors to consistent categories (404 / 503 / 504 / 502 / 400); never include URLs, headers, or signatures in error messages (`SecretRedactor`).
9. **Per-request logging:** Image ID, provider, cache hit/miss, duration, bytes, and status/error category. Never log SAS, signatures, account keys, signed URLs, or Authorization.
10. **Cache precedes storage; authorization precedes cache** (ADR 0017): cache hits do not invoke the broker or storage.

## Consequences

- All remote access passes through the server; clients never see cloud addresses. The trade-off is image bandwidth through the application, mitigated by server and browser caching.
- Additional operational roles: *Storage Blob Delegator* (plus a data role) for the application identity and potentially private-endpoint access.
- Outstanding verification: user-delegation SAS and real Entra authentication have not been tested against an actual Azure account; S3 has only a test vector/unit test, and SharePoint only a Graph mock.
