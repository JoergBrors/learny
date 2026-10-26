# 0017 — Configurable Image Storage Providers, Image Cache, and Explicit Migration

- **Status:** Accepted
- **Date:** 2026-10-25
- **Partially supersedes:** [0016](0016-bilder-in-lernkarten.md), storage only (inline Base64 in `cards.images_json`). The `images[]` import/API format, limits (5 images, 2 MiB, 300-character text), and validation from ADR 0016 remain unchanged.
- **Supplemented by:** [0018](0018-zentraler-image-proxy-und-storage-zugriff.md) (delivery, SAS/presigned URLs, SSRF protection)
- **Supplements:** [0004](0004-datenbank-abstraktion.en.md) (schema), [0011](0011-mehrbenutzerarchitektur.en.md)/[0015](0015-freigaben-globale-lernmodule.md) (permissions)

## Context

ADR 0016 stored images as Base64 strings in the card row. This expands each card query, prevents browser caching, and ties images to the database. Deployment environments (IIS, Docker, Azure) need alternative storage options (file system, Azure Blob, S3-compatible, SharePoint). Permissions (private/shared/global), import JSON, and MCP tools must remain independent of the underlying byte storage.

## Decision

1. **Provider interface:** `IImageStorageProvider` (binary, `CancellationToken`) with `PutAsync`, `GetAsync` (optional ETag), `ExistsAsync`, `DeleteAsync`, and `CheckHealthAsync`. Storage keys are **server-generated** GUIDs (32 hexadecimal characters, `ImageStorageKey`) and revalidated by every provider (preventing path, URL, or header manipulation).
2. **Configuration:** Environment variables (`IMAGE_STORAGE_PROVIDER=database|local|azureblob|s3|sharepoint`, default `database`). Secrets come only from the environment or a secret manager, never from source code. Complete startup validation (`ImageStorageSettings.Validate`) rejects invalid settings with readable errors. Exactly one **primary** provider receives new images per installation; other configured providers remain readable.
3. **Database metadata:** `card_images` table (`id` = `image_id`, `card_id`, `provider`, `storage_key`, `mime_type`, `size_bytes`, SHA-256 `checksum`, `alt_text`, `caption`, `sort_order`). The database provider puts bytes in `image_blobs` (Base64 **text**, since the `IDatabase` protocol is text-based per ADR 0004, although the provider interface is binary). Bytes with the same checksum within a card are deduplicated; cleanup uses reference counts (`CleanupOrphansAsync`).
4. **Existing data:** On startup, cards with `images_json` are migrated once into `card_images` (`cards.images_migrated`). The original `images_json` is retained **unchanged** (no data loss; rollback possible). An individual card's migration error does not prevent startup.
5. **Providers:**
   - *database* — default, backward compatible.
   - *local* — `{IMAGE_LOCAL_PATH}/{key[0..2]}/{key}.img`; atomic writes (temporary file and rename with retries on Windows), permissions 0600/0700 on Linux/macOS, persistent container volume.
   - *azureblob* — REST via `HttpClient` (no SDK). Data-plane writes/deletes/checks use an Entra token (prefer managed identity; client secret supported) or SharedKey (development/Azurite only); **reads use read-only SAS** (ADR 0018).
   - *s3* — S3-compatible (AWS, MinIO, etc.), custom SigV4 implementation (tested against an official AWS test vector), optional path-style addressing, fixed configured endpoint; reads through presigned URLs (ADR 0018).
   - *sharepoint* — Microsoft Graph, **app-only** (client credentials). User login tokens are never used with Graph; LearnCards alone checks permissions. Minimum permission: application-level `Sites.Selected`, granting write access to precisely the specified site/library, not `Sites.ReadWrite.All`.
6. **Image cache:** `IImageCache` (in-memory LRU plus optional persistent file cache; `TwoLevelImageCache`) applies only to **remote** providers. Key = provider + image ID + checksum (changed content yields a new entry). Cache hits avoid provider access; concurrent requests for the same image are coalesced (single-flight). Size-based eviction applies. Following TTL expiration, ETag revalidation is used (conditional request for Azure/S3; SharePoint metadata ETag without downloading). Files use SHA-256 names and checksums are verified on read. Temporary download URLs (Graph `downloadUrl`, SAS) are **never stored**. The cache is **behind** authorization checks and never bypasses them. Default file cache exists only for SharePoint (`data/image-cache`), otherwise configure `IMAGE_CACHE_PATH`. Settings: `IMAGE_CACHE_ENABLED` (true), `IMAGE_CACHE_MAX_SIZE_MB` (512), `IMAGE_CACHE_TTL_MINUTES` (1440), `IMAGE_CACHE_MEMORY_MB` (64), `IMAGE_CACHE_PATH`.
7. **Migration:** `ImageMigrationService`, admin endpoints `GET /api/admin/images/storage` and `POST /api/admin/images/migrate`. Migration starts **only explicitly**, never automatically upon provider changes. For each object: read source → validate checksum → write target → read back and validate checksum → then update `card_images.provider`. **Never delete the source.** Process in resumable/idempotent batches (`limit`, `cursor`), support `dry_run`, log `images.migrate`; require the `admin` role and browser login.
8. **Import/export:** `images[]` with `data_base64` remains the only input mechanism (REST, MCP, file). Entries containing only `image_id` retain an existing image. `GET /api/cards/{id}` returns metadata and a URL; Base64 image bytes are included only with `?include_image_data=true` (export/backup).

## Rejected Alternatives

- *Binary column (`bytea`/BLOB):* `IDatabase` transfers values as text; implementing binary pathways in both drivers adds complexity without benefit at the target size (≤2 MiB, ≤5 images/card).
- *Cloud SDKs:* violate ADR 0002 (no NuGet packages); REST and custom signing are manageable and testable.
- *Automatic provider-switch migration:* risky and difficult to reverse; instead, migrate explicitly with verification and no deletion.

## Consequences

- Card queries stay small (metadata instead of Base64); browsers cache images (`ETag`, `Cache-Control: private`).
- Switching providers alone does not move existing images: they remain readable through the old provider while it is configured (`NotConfigured` → HTTP 503 with explanation), until migration runs.
- Additional operational responsibility: manage access and roles for the selected storage service.

## Measurements (2026-10-25 Test; Local Services, SQLite, One Machine)

Store three images (361 KB); retrieve one 300 KB image, median / P95 of 20 requests (application → proxy → storage or cache):

| Provider | Store | Fetch (median / P95) |
|---|---|---|
| database | 93 ms | 4 / 22 ms |
| local | 100 ms | 2 / 3 ms |
| azureblob (Azurite, cache on) | 133 ms | 1 / 1 ms |
| sharepoint (Graph mock, cache on) | 92 ms | 0 / 1 ms |

These values represent only application overhead using local services (no network; repeated remote-provider reads hit the cache). Actual cloud latency was not measured and is relevant to first requests and post-expiration cache misses.
