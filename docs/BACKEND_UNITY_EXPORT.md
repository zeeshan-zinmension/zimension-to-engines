# Backend contract: Unity export links

The backend endpoints that let a Unity user paste a link and import a Zimension project. The
browser keeps building the export zip with `Engine.generateApp` (the same packaging and permission
checks as the Web export); the backend only stores it and hands it out.

`tools/dev-export-server.mjs` implements this contract with no dependencies, storing files on
disk. Use it to test the Unity side now, and as the behavioural reference for the real
implementation. The Unity downloader's tests run against the same contract.

All routes sit under the existing API base: `https://api.zimension3d.com/api/v1/world/unity-export`.
Errors use the existing `{ success: false, error }` / `{ success: false, message }` body.

## Model: one share per world, many versions

| Field | Notes |
|---|---|
| `code` | Link token. At least 96 bits of randomness, URL-safe (`[A-Za-z0-9_-]{16,64}`), unique, indexed. Anyone with the code can download, like a signed download link. |
| `worldId`, `ownerId` | The world and the user who created the share. |
| `versions[]` | `{ version, storageKey, size, sha256, createdAt }`, newest last. Keep the last 3 and delete older files. |
| `createdAt`, `revokedAt`, `expiresAt` | `expiresAt` is null for "until revoked", or a date. |

The share is created by the first upload for `(worldId, ownerId)` and reused by later uploads, so
the link a user pasted into Unity keeps pointing at the newest export. That is what **Pull latest**
relies on.

## 1. Upload an export

`POST /world/unity-export/:worldId`

* **Auth:** `Authorization: Bearer <token>`, the same header and token as `/world/app-build`.
  The user must be allowed to export this world: owner or editor, with the plan that allows
  project export. These are the same checks as app builds. The play-only native-player
  credential must **not** be accepted.
* **Body:** `multipart/form-data` with one file field `export` (the zip, `application/zip`).
  Reject anything over 1 GB with 413, and an empty or non-zip body with 400. The file must start
  with `PK\x03\x04`.
* The server computes `sha256` and `size` while streaming to storage. Don't buffer the whole file
  in memory.
* **Response `201`:**

```json
{
  "success": true,
  "code": "Qe9x3...k2",
  "link": "https://api.zimension3d.com/api/v1/world/unity-export/link/Qe9x3...k2",
  "version": "2026-11-04T20:15:03.120Z",
  "size": 48213377,
  "sha256": "9f2c...e1",
  "expiresAt": null
}
```

`version` is unique per upload. An ISO timestamp is fine, as long as it changes for every upload.

## 2. Resolve a link (what Unity calls)

`GET /world/unity-export/link/:code`

* **No auth:** the code is the capability. Rate-limit by IP (for example, 60 per minute).
* **`200`:**

```json
{
  "success": true,
  "worldId": "64f0...",
  "worldName": "Billionaires' Meeting",
  "version": "2026-11-04T20:15:03.120Z",
  "size": 48213377,
  "sha256": "9f2c...e1",
  "expiresAt": null,
  "downloadUrl": "https://<storage>/...signed...  or  /api/v1/world/unity-export/link/Qe9x3...k2/download"
}
```

* `downloadUrl` can be absolute (a signed S3/CloudFront URL valid for at least 1 hour) or
  relative to the API host.
* **`404`:** unknown code. **`410`:** revoked or expired. The Unity side shows a specific message
  for each.

## 3. Download

`GET /world/unity-export/link/:code/download`, or the signed storage URL.

* Streams the newest version's zip, with `Content-Type: application/zip`, `Content-Length`,
  `Content-Disposition: attachment; filename="<world>-unity.zip"` and `ETag: "<sha256>"`.
* **Must support `Range: bytes=N-`** (`206` with `Content-Range`) and `If-Range`, so interrupted
  downloads of large projects resume. S3 and CloudFront signed URLs already do.

## 4. Revoke (optional in Phase 1)

`DELETE /world/unity-export/:worldId`, with auth (owner). This sets `revokedAt`; the link then
returns `410`. The next upload creates a new code.

## Security notes

* Never log codes or signed URLs. Exports contain the creator's full assets; treat a code like a
  password-protected download link.
* Delete shares and files when the world or account is deleted.
* Phase 2 (sign in from Unity) adds a device-code login with an **export-scoped** token, plus
  `GET /world/unity-export/mine` returning the user's shares. The download contract stays the same.
