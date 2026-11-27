import { spawn } from 'node:child_process';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import { fileURLToPath } from 'node:url';

/**
 * Smoke test for game/server.js: boots it on a free port and checks that the
 * browser companion and the shared JSON are actually served (and that traversal
 * and dotfiles are refused). No dependencies.
 *
 *   node tools/server_smoke.mjs
 */
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.on('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
  });
}

const port = await freePort();
const child = spawn(process.execPath, ['server.js'], {
  cwd: path.join(root, 'game'),
  env: { ...process.env, PORT: String(port), HOST: '127.0.0.1' },
  stdio: ['ignore', 'pipe', 'pipe'],
});
let logs = '';
child.stdout.on('data', (d) => (logs += d));
child.stderr.on('data', (d) => (logs += d));

const base = `http://127.0.0.1:${port}`;

/** Raw request: fetch() normalises "../" away, so traversal needs the wire path verbatim. */
function rawGet(pathname) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, method: 'GET', path: pathname }, (res) => {
      res.resume();
      res.on('end', () => resolve({ status: res.statusCode }));
    });
    req.on('error', reject);
    req.end();
  });
}
async function waitReady() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(`${base}/healthz`);
      if (r.ok) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error('server did not become ready\n' + logs);
}

const checks = [];
const check = (name, ok) => checks.push([name, ok]);
const FILES = ['config', 'buildings', 'races', 'units', 'heroes', 'skills', 'gear', 'dragons', 'world'];

try {
  await waitReady();

  const rootRes = await fetch(`${base}/`, { redirect: 'manual' });
  check('GET / redirects to /preview/', rootRes.status === 302 && rootRes.headers.get('location') === '/preview/');

  const page = await fetch(`${base}/preview/`);
  const html = await page.text();
  check('preview page served as html', page.status === 200 && /text\/html/.test(page.headers.get('content-type') ?? '')
    && html.includes('id="grid"') && html.includes('app.js'));

  for (const asset of ['/preview/app.js', '/preview/sim.mjs', '/preview/style.css']) {
    const r = await fetch(base + asset);
    const type = r.headers.get('content-type') ?? '';
    const wantJs = /\b(js|mjs)$/.test(asset);
    check(`${asset} served (${wantJs ? 'js' : 'css'})`, r.ok && (wantJs ? /javascript/.test(type) : /css/.test(type)));
  }

  let dataOk = 0;
  for (const n of FILES) {
    const r = await fetch(`${base}/Assets/Resources/Data/${n}.json`);
    if (!r.ok) continue;
    try {
      JSON.parse(await r.text());
      dataOk++;
    } catch {}
  }
  check(`all ${FILES.length} data files served as valid JSON`, dataOk === FILES.length);

  const missing = await fetch(`${base}/preview/nope.js`);
  check('missing file -> 404', missing.status === 404);

  const git = await rawGet('/.git/config');
  check('dotfile (.git/config) -> 403', git.status === 403);

  // Escapes are clamped at the repo root (404) or rejected outright (403) — never served.
  for (const attack of ['/preview/../../etc/passwd', '/preview/%2e%2e/%2e%2e/etc/passwd', '/..%2f.git/config']) {
    const r = await rawGet(attack);
    check(`path traversal ${attack} refused (${r.status})`, r.status === 403 || r.status === 404);
  }

  const post = await fetch(`${base}/preview/`, { method: 'POST' });
  check('non-GET method -> 405', post.status === 405);
} catch (e) {
  console.log('RUNTIME ERROR', e.message);
  check('server boots and accepts connections', false);
} finally {
  child.kill('SIGTERM');
}

let failed = 0;
for (const [name, ok] of checks) {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`);
  if (!ok) failed++;
}
console.log(`\n${checks.length - failed}/${checks.length} server checks passed.`);
process.exitCode = failed ? 1 : 0;
