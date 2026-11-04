#!/usr/bin/env node
// Reference implementation of the Unity export link contract (docs/BACKEND_UNITY_EXPORT.md),
// for local testing and as the behavioural spec for the real backend. No dependencies; files are
// stored on disk. NOT for production: there is no real authentication (any Bearer token is
// accepted unless --token is given) and no rate limiting.
//
//   node tools/dev-export-server.mjs [--port 8787] [--data .dev-exports] [--token secret]
//                                    [--publish <export.zip | export folder> [--world <id>]]
//
// --publish shares a local export (a zip, or a folder such as samples/event-fixture, which is
// zipped on the fly) and prints its link. Paste that link (or just the code) into Unity:
// Zimension > Import from Zimension, with the API base set to http://localhost:<port>/api/v1.
//
// Endpoints (under /api/v1/world/unity-export):
//   POST   /:worldId               multipart/form-data, file field "export" (sent last)  -> 201 {code, link, version, size, sha256}
//   GET    /link/:code             -> {worldId, worldName, version, size, sha256, expiresAt, downloadUrl}
//   GET    /link/:code/download    -> zip; Range / If-Range / ETag supported
//   DELETE /:worldId               -> revoke the world's share (410 afterwards)

import http from "node:http";
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import crypto from "node:crypto";
import { pipeline } from "node:stream/promises";

const args = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const PORT = Number(arg("--port", process.env.PORT || 8787));
const DATA = path.resolve(arg("--data", ".dev-exports"));
const TOKEN = arg("--token", "");
const MAX_BYTES = 1024 * 1024 * 1024;
const KEEP_VERSIONS = 3;
const BASE = "/api/v1/world/unity-export";

await fsp.mkdir(path.join(DATA, "files"), { recursive: true });
const sharesPath = path.join(DATA, "shares.json");
let shares = {};
try { shares = JSON.parse(await fsp.readFile(sharesPath, "utf8")); } catch { shares = {}; }
const save = () => fsp.writeFile(sharesPath, JSON.stringify(shares, null, 2));

const newCode = () => crypto.randomBytes(16).toString("base64url"); // 128 bits, 22 chars

const findShare = (worldId, ownerId) =>
  Object.values(shares).find((s) => s.worldId === worldId && s.ownerId === ownerId && !s.revokedAt);

const send = (res, status, body, headers = {}) => {
  const text = JSON.stringify(body);
  res.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(text), ...headers });
  res.end(text);
};

const cors = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "Authorization, Content-Type, Range, If-Range",
  "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
  "Access-Control-Expose-Headers": "Content-Length, Content-Range, ETag, Content-Disposition",
};

// ---- Storage ------------------------------------------------------------------------------

async function sha256File(file) {
  const hash = crypto.createHash("sha256");
  await pipeline(fs.createReadStream(file), hash);
  return hash.digest("hex");
}

async function readHead(file, bytes) {
  const handle = await fsp.open(file, "r");
  try {
    const buffer = Buffer.alloc(bytes);
    const { bytesRead } = await handle.read(buffer, 0, bytes, 0);
    return buffer.subarray(0, bytesRead);
  } finally { await handle.close(); }
}

/** Store a zip file as the newest version of the world's share; returns the share and version. */
async function publish(zipFile, worldId, ownerId, worldName) {
  const head = await readHead(zipFile, 4);
  if (head.length < 4 || head[0] !== 0x50 || head[1] !== 0x4b || head[2] !== 0x03 || head[3] !== 0x04)
    throw Object.assign(new Error("The export is not a zip file"), { status: 400 });
  const size = (await fsp.stat(zipFile)).size;
  if (size === 0) throw Object.assign(new Error("Empty export"), { status: 400 });
  if (size > MAX_BYTES) throw Object.assign(new Error("Export over 1 GB"), { status: 413 });
  const sha256 = await sha256File(zipFile);
  let share = findShare(worldId, ownerId);
  if (!share) {
    share = { code: newCode(), worldId, ownerId, worldName: worldName || worldId, versions: [], createdAt: new Date().toISOString(), revokedAt: null, expiresAt: null };
    shares[share.code] = share;
  }
  if (worldName) share.worldName = worldName;
  const version = new Date().toISOString();
  const storageKey = `${share.code}-${Date.now()}-${crypto.randomBytes(4).toString("hex")}.zip`;
  await fsp.rename(zipFile, path.join(DATA, "files", storageKey)).catch(async () => {
    await fsp.copyFile(zipFile, path.join(DATA, "files", storageKey));
    await fsp.unlink(zipFile).catch(() => {});
  });
  share.versions.push({ version, storageKey, size, sha256, createdAt: version });
  while (share.versions.length > KEEP_VERSIONS) {
    const old = share.versions.shift();
    await fsp.unlink(path.join(DATA, "files", old.storageKey)).catch(() => {});
  }
  await save();
  return { share, latest: share.versions[share.versions.length - 1] };
}

// ---- Minimal zip writer (stored entries) for --publish <folder> ----------------------------

const crcTable = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

async function zipFolder(folder, out) {
  const files = [];
  const walk = async (dir) => {
    for (const entry of await fsp.readdir(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) await walk(full);
      else files.push(full);
    }
  };
  await walk(folder);
  const chunks = [], central = [];
  let offset = 0;
  for (const file of files) {
    const name = Buffer.from(path.relative(folder, file).split(path.sep).join("/"));
    const data = await fsp.readFile(file);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x0800, 6); local.writeUInt16LE(0, 8);
    local.writeUInt32LE(0, 10); local.writeUInt32LE(crc, 14); local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26); local.writeUInt16LE(0, 28);
    chunks.push(local, name, data);
    const header = Buffer.alloc(46);
    header.writeUInt32LE(0x02014b50, 0); header.writeUInt16LE(20, 4); header.writeUInt16LE(20, 6); header.writeUInt16LE(0x0800, 8);
    header.writeUInt16LE(0, 10); header.writeUInt32LE(0, 12); header.writeUInt32LE(crc, 16); header.writeUInt32LE(data.length, 20);
    header.writeUInt32LE(data.length, 24); header.writeUInt16LE(name.length, 28); header.writeUInt32LE(offset, 42);
    central.push(header, name);
    offset += 30 + name.length + data.length;
  }
  const centralBuffer = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralBuffer.length, 12); end.writeUInt32LE(offset, 16);
  await fsp.writeFile(out, Buffer.concat([...chunks, centralBuffer, end]));
}

// ---- Multipart upload (file field "export", sent as the last part) ---------------------------

async function receiveUpload(req) {
  const type = req.headers["content-type"] || "";
  const match = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(type);
  if (!type.startsWith("multipart/form-data") || !match) throw Object.assign(new Error("Send multipart/form-data with a file field named 'export'"), { status: 400 });
  const boundary = Buffer.from("--" + (match[1] || match[2]).trim());
  const raw = path.join(DATA, `upload-${Date.now()}-${crypto.randomBytes(4).toString("hex")}.raw`);
  let received = 0;
  req.on("data", (chunk) => {
    received += chunk.length;
    if (received > MAX_BYTES + 1024 * 1024) req.destroy(Object.assign(new Error("Export over 1 GB"), { status: 413 }));
  });
  await pipeline(req, fs.createWriteStream(raw));
  try {
    const size = (await fsp.stat(raw)).size;
    const head = await readHead(raw, Math.min(size, 1024 * 1024));
    // Find the part whose headers name the "export" field.
    let at = 0, start = -1, fields = {};
    while ((at = head.indexOf(boundary, at)) >= 0) {
      const headerStart = at + boundary.length + 2;
      const headerEnd = head.indexOf("\r\n\r\n", headerStart);
      if (headerEnd < 0) break;
      const headers = head.subarray(headerStart, headerEnd).toString("utf8");
      const name = /name="([^"]*)"/i.exec(headers)?.[1];
      if (name === "export") { start = headerEnd + 4; break; }
      const valueEnd = head.indexOf(boundary, headerEnd + 4);
      if (name && valueEnd > 0) fields[name] = head.subarray(headerEnd + 4, valueEnd - 2).toString("utf8");
      at = headerEnd;
    }
    if (start < 0) throw Object.assign(new Error("No file field named 'export'"), { status: 400 });
    // The file is the last part: it ends at "\r\n--boundary--" near the end.
    const tailLength = Math.min(size, 4096);
    const handle = await fsp.open(raw, "r");
    const tail = Buffer.alloc(tailLength);
    await handle.read(tail, 0, tailLength, size - tailLength);
    await handle.close();
    const closing = tail.lastIndexOf(Buffer.concat([Buffer.from("\r\n"), boundary]));
    if (closing < 0) throw Object.assign(new Error("Malformed multipart body"), { status: 400 });
    const end = size - tailLength + closing; // exclusive
    if (end <= start) throw Object.assign(new Error("Empty export"), { status: 400 });
    const out = raw.replace(/\.raw$/, ".zip");
    await pipeline(fs.createReadStream(raw, { start, end: end - 1 }), fs.createWriteStream(out));
    return { file: out, fields };
  } finally {
    await fsp.unlink(raw).catch(() => {});
  }
}

// ---- HTTP ---------------------------------------------------------------------------------

function authUser(req) {
  const header = req.headers.authorization || "";
  const token = header.startsWith("Bearer ") ? header.slice(7).trim() : "";
  if (!token) return null;
  if (TOKEN && token !== TOKEN) return null;
  return "dev-user:" + crypto.createHash("sha256").update(token).digest("hex").slice(0, 12);
}

async function download(req, res, share) {
  const latest = share.versions[share.versions.length - 1];
  const file = path.join(DATA, "files", latest.storageKey);
  const size = latest.size;
  const etag = `"${latest.sha256}"`;
  const name = `${String(share.worldName || "zimension").replace(/[^a-zA-Z0-9._-]+/g, "-")}-unity.zip`;
  const headers = { ...cors, "Content-Type": "application/zip", "Accept-Ranges": "bytes", ETag: etag, "Content-Disposition": `attachment; filename="${name}"` };
  const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
  const ifRange = req.headers["if-range"];
  if (range && (!ifRange || ifRange === etag)) {
    const start = Number(range[1]);
    const end = range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    if (start >= size || start > end) {
      res.writeHead(416, { ...headers, "Content-Range": `bytes */${size}` });
      return res.end();
    }
    res.writeHead(206, { ...headers, "Content-Range": `bytes ${start}-${end}/${size}`, "Content-Length": end - start + 1 });
    return pipeline(fs.createReadStream(file, { start, end }), res).catch(() => {});
  }
  res.writeHead(200, { ...headers, "Content-Length": size });
  return pipeline(fs.createReadStream(file), res).catch(() => {});
}

const server = http.createServer(async (req, res) => {
  try {
    if (req.method === "OPTIONS") { res.writeHead(204, cors); return res.end(); }
    const url = new URL(req.url, `http://${req.headers.host}`);
    if (!url.pathname.startsWith(BASE + "/")) return send(res, 404, { success: false, error: "Not found" }, cors);
    const rest = url.pathname.slice(BASE.length + 1).split("/").filter(Boolean);
    if (rest[0] === "link" && rest[1] && req.method === "GET") {
      const share = shares[rest[1]];
      if (!share || share.versions.length === 0) return send(res, 404, { success: false, error: "Export link not found" }, cors);
      if (share.revokedAt || (share.expiresAt && Date.parse(share.expiresAt) < Date.now()))
        return send(res, 410, { success: false, error: "This export link has expired or was revoked" }, cors);
      if (rest[2] === "download") return download(req, res, share);
      const latest = share.versions[share.versions.length - 1];
      return send(res, 200, {
        success: true, worldId: share.worldId, worldName: share.worldName, version: latest.version,
        size: latest.size, sha256: latest.sha256, expiresAt: share.expiresAt,
        downloadUrl: `${BASE}/link/${share.code}/download`,
      }, cors);
    }
    if (rest.length === 1 && rest[0] !== "link" && (req.method === "POST" || req.method === "DELETE")) {
      const user = authUser(req);
      if (!user) return send(res, 401, { success: false, error: "Sign in to export" }, cors);
      const worldId = rest[0];
      if (req.method === "DELETE") {
        const share = findShare(worldId, user);
        if (!share) return send(res, 404, { success: false, error: "No share for this world" }, cors);
        share.revokedAt = new Date().toISOString();
        await save();
        return send(res, 200, { success: true }, cors);
      }
      const upload = await receiveUpload(req);
      const { share, latest } = await publish(upload.file, worldId, user, upload.fields.worldName);
      const origin = `http://${req.headers.host}`;
      return send(res, 201, {
        success: true, code: share.code, link: `${origin}${BASE}/link/${share.code}`,
        version: latest.version, size: latest.size, sha256: latest.sha256, expiresAt: share.expiresAt,
      }, cors);
    }
    return send(res, 404, { success: false, error: "Not found" }, cors);
  } catch (error) {
    if (!res.headersSent) send(res, error.status || 500, { success: false, error: error.message }, cors);
    else res.destroy();
  }
});

const publishPath = arg("--publish", "");
if (publishPath) {
  const resolved = path.resolve(publishPath);
  const temp = path.join(DATA, `publish-${Date.now()}.zip`);
  if ((await fsp.stat(resolved)).isDirectory()) await zipFolder(resolved, temp);
  else await fsp.copyFile(resolved, temp);
  const worldId = arg("--world", path.basename(resolved).replace(/\.zip$/i, ""));
  const { share, latest } = await publish(temp, worldId, "dev-user:cli", worldId);
  console.log(`Published ${publishPath} (${latest.size} bytes, sha256 ${latest.sha256.slice(0, 12)}...)`);
  console.log(`  Code: ${share.code}`);
  console.log(`  Link: http://localhost:${PORT}${BASE}/link/${share.code}`);
}

server.listen(PORT, () => console.log(`Zimension export dev server on http://localhost:${PORT}${BASE} (data: ${DATA})`));
