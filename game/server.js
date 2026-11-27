/**
 * Nore — zero-dependency static server for the browser companion.
 *
 *   cd game && node server.js          # http://localhost:3000
 *   PORT=8080 node server.js           # custom port
 *
 * Why it exists: the companion is an ES-module app that fetches the shared JSON in
 * ../Assets/Resources/Data, which the browser blocks on file:// (CORS). This server
 * exposes the repository root while keeping the friendly URL / -> preview/.
 *
 * No dependencies, binds 0.0.0.0 (works in sandboxes / live previews), honours PORT.
 */
'use strict';

const http = require('node:http');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.PORT || 3000);
const HOST = process.env.HOST || '0.0.0.0';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.ico': 'image/x-icon',
  '.webp': 'image/webp',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
  '.map': 'application/json; charset=utf-8',
};

function contentType(file) {
  return MIME[path.extname(file).toLowerCase()] || 'application/octet-stream';
}

/** Resolve a URL path inside ROOT, or null when it escapes the root. */
function resolveSafe(urlPath) {
  let decoded;
  try {
    decoded = decodeURIComponent(urlPath);
  } catch {
    return null; // malformed %-escape
  }
  if (decoded.includes('\0')) return null;
  const normalized = path.posix.normalize('/' + decoded);
  // Never expose dotfiles / .git (saves, keys, repo internals) over HTTP.
  if (normalized.split('/').some((seg) => seg.startsWith('.'))) return null;
  const full = path.resolve(ROOT, '.' + normalized);
  const rel = path.relative(ROOT, full);
  if (rel.startsWith('..') || path.isAbsolute(rel)) return null;
  return full;
}

function send(res, status, body, headers = {}) {
  res.writeHead(status, { 'Cache-Control': 'no-cache', ...headers });
  res.end(body);
}

async function statOrNull(p) {
  try {
    return await fsp.stat(p);
  } catch {
    return null;
  }
}

async function serveFile(req, res, file) {
  const st = await statOrNull(file);
  if (!st || !st.isFile()) return false;
  res.writeHead(200, {
    'Content-Type': contentType(file),
    'Content-Length': st.size,
    'Cache-Control': 'no-cache',
  });
  if (req.method === 'HEAD') return res.end(), true;
  await new Promise((resolve, reject) => {
    const stream = fs.createReadStream(file);
    stream.on('error', reject);
    stream.on('end', resolve);
    stream.pipe(res);
  });
  return true;
}

async function listDir(dir, urlPath) {
  const entries = await fsp.readdir(dir, { withFileTypes: true });
  const rows = entries
    .filter((e) => !e.name.startsWith('.'))
    .sort((a, b) => Number(b.isDirectory()) - Number(a.isDirectory()) || a.name.localeCompare(b.name))
    .map((e) => {
      const name = e.name + (e.isDirectory() ? '/' : '');
      const href = path.posix.join(urlPath, encodeURIComponent(e.name)) + (e.isDirectory() ? '/' : '');
      return `<li><a href="${href}">${escapeHtml(name)}</a></li>`;
    })
    .join('\n');
  return `<!doctype html><meta charset="utf-8"><title>Index of ${escapeHtml(urlPath)}</title>
<style>body{font:14px/1.5 system-ui,sans-serif;background:#0b1018;color:#e6ecf7;padding:24px}
a{color:#3f8fe0}li{margin:2px 0}code{color:#93a4bd}</style>
<h1>Index of ${escapeHtml(urlPath)}</h1><ul>${rows}</ul>
<p><a href="/">→ game (preview/)</a></p>`;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const server = http.createServer(async (req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    return send(res, 405, 'Method Not Allowed', { Allow: 'GET, HEAD', 'Content-Type': 'text/plain' });
  }

  let pathname;
  try {
    pathname = new URL(req.url, 'http://localhost').pathname;
  } catch {
    return send(res, 400, 'Bad Request', { 'Content-Type': 'text/plain' });
  }

  // Tiny health endpoint (useful for probes / smoke tests).
  if (pathname === '/healthz') {
    return send(res, 200, JSON.stringify({ ok: true, root: ROOT }), {
      'Content-Type': 'application/json; charset=utf-8',
    });
  }

  // Friendly entry points.
  if (pathname === '/' || pathname === '/index.html') {
    return send(res, 302, '', { Location: '/preview/' });
  }
  if (pathname === '/preview') {
    return send(res, 301, '', { Location: '/preview/' });
  }

  const file = resolveSafe(pathname);
  if (!file) return send(res, 403, 'Forbidden', { 'Content-Type': 'text/plain' });

  const st = await statOrNull(file);
  if (!st) return send(res, 404, `Not found: ${pathname}`, { 'Content-Type': 'text/plain; charset=utf-8' });

  try {
    if (st.isDirectory()) {
      if (!pathname.endsWith('/')) return send(res, 301, '', { Location: pathname + '/' });
      const index = path.join(file, 'index.html');
      if (await serveFile(req, res, index)) return;
      const html = await listDir(file, pathname);
      return send(res, 200, html, { 'Content-Type': 'text/html; charset=utf-8' });
    }
    await serveFile(req, res, file);
  } catch (err) {
    if (!res.headersSent) send(res, 500, 'Internal Server Error', { 'Content-Type': 'text/plain' });
    else res.destroy();
    console.error(`[server] ${req.method} ${pathname} ->`, err.message);
  }
});

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`Port ${PORT} is busy. Try: PORT=${PORT + 1} node server.js`);
  } else {
    console.error('[server]', err);
  }
  process.exit(1);
});

server.listen(PORT, HOST, () => {
  const shown = HOST === '0.0.0.0' ? 'localhost' : HOST;
  console.log(`Nore browser companion → http://${shown}:${PORT}`);
  console.log(`  serving ${ROOT}`);
  console.log(`  preview  /preview/   data  /Assets/Resources/Data/`);
});

for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, () => server.close(() => process.exit(0)));
}
