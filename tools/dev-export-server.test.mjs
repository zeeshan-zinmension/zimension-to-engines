// Contract tests for tools/dev-export-server.mjs (docs/BACKEND_UNITY_EXPORT.md).
// Usage: node tools/dev-export-server.test.mjs
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..");
const data = await fs.mkdtemp(path.join(os.tmpdir(), "zim-exports-"));
const port = 18000 + Math.floor(Math.random() * 2000);
const base = `http://localhost:${port}/api/v1/world/unity-export`;
let failures = 0, checks = 0;
const expect = (ok, what) => { checks++; if (!ok) { failures++; console.error("FAIL:", what); } };

const server = spawn(process.execPath, [path.join(here, "dev-export-server.mjs"), "--port", String(port), "--data", data,
  "--publish", path.join(repo, "samples", "event-fixture"), "--world", "fixture"], { stdio: ["ignore", "pipe", "inherit"] });
let output = "";
await new Promise((resolve, reject) => {
  server.stdout.on("data", (d) => { output += d; if (output.includes("dev server on")) resolve(); });
  server.on("exit", (code) => reject(new Error("server exited " + code)));
  setTimeout(() => reject(new Error("server did not start")), 10000);
});
try {
  const code = /Code: (\S+)/.exec(output)[1];
  expect(/^[A-Za-z0-9_-]{16,64}$/.test(code), "published code is URL-safe and long enough");
  const info = await (await fetch(`${base}/link/${code}`)).json();
  expect(info.success && info.worldId === "fixture" && info.size > 0 && /^[0-9a-f]{64}$/.test(info.sha256), "resolve returns export details");
  const full = Buffer.from(await (await fetch(new URL(info.downloadUrl, base))).arrayBuffer());
  expect(full.length === info.size && crypto.createHash("sha256").update(full).digest("hex") === info.sha256, "full download matches size and sha256");
  expect(full[0] === 0x50 && full[1] === 0x4b, "download is a zip");
  const ranged = await fetch(new URL(info.downloadUrl, base), { headers: { Range: "bytes=100-", "If-Range": `"${info.sha256}"` } });
  const tail = Buffer.from(await ranged.arrayBuffer());
  expect(ranged.status === 206 && tail.equals(full.subarray(100)), "Range + matching If-Range resumes with 206");
  const stale = await fetch(new URL(info.downloadUrl, base), { headers: { Range: "bytes=100-", "If-Range": `"stale"` } });
  expect(stale.status === 200 && Buffer.from(await stale.arrayBuffer()).length === info.size, "stale If-Range returns the whole file");
  const beyond = await fetch(new URL(info.downloadUrl, base), { headers: { Range: `bytes=${info.size + 10}-` } });
  expect(beyond.status === 416, "range beyond the end is 416");

  // Upload through the API (multipart, extra field first, file last): same world + user keeps the link.
  const upload = async (token, bytes, world = "w-upload") => {
    const form = new FormData();
    form.append("worldName", "Uploaded World");
    form.append("export", new Blob([bytes], { type: "application/zip" }), "x-unity.zip");
    return fetch(`${base}/${world}`, { method: "POST", headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
  };
  const first = await upload("tok-a", full);
  const firstBody = await first.json();
  expect(first.status === 201 && firstBody.link.endsWith(firstBody.code) && firstBody.sha256 === info.sha256, "upload returns 201 with link and sha256");
  await new Promise((r) => setTimeout(r, 5));
  const second = await (await upload("tok-a", full)).json();
  expect(second.code === firstBody.code && second.version !== firstBody.version, "re-upload keeps the link and changes the version");
  const other = await (await upload("tok-b", full)).json();
  expect(other.code !== firstBody.code, "another user gets their own link");
  const named = await (await fetch(`${base}/link/${firstBody.code}`)).json();
  expect(named.worldName === "Uploaded World", "worldName field is stored");
  expect((await upload("", full)).status === 401, "upload without auth is 401");
  expect((await upload("tok-a", Buffer.from("not a zip"))).status === 400, "non-zip upload is 400");
  expect((await fetch(`${base}/link/doesnotexist123`)).status === 404, "unknown code is 404");
  const revoke = await fetch(`${base}/w-upload`, { method: "DELETE", headers: { Authorization: "Bearer tok-a" } });
  expect(revoke.status === 200 && (await fetch(`${base}/link/${firstBody.code}`)).status === 410, "revoked link is 410");
} finally {
  server.kill();
  await fs.rm(data, { recursive: true, force: true });
}
if (failures) { console.error(`${failures} of ${checks} contract checks failed`); process.exit(1); }
console.log(`Dev export server contract checks passed (${checks}).`);
