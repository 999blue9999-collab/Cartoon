import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
// jsdom is installed in tools/.dom:  cd tools/.dom && npm init -y && npm install jsdom
const jsdomPath = path.join(root, 'tools', '.dom', 'node_modules', 'jsdom', 'lib', 'api.js');
if (!fs.existsSync(jsdomPath)) {
  console.error('Missing jsdom. Run: cd tools/.dom && npm init -y && npm install jsdom');
  process.exit(2);
}
const { JSDOM } = await import(pathToFileURL(jsdomPath).href);
const html = fs.readFileSync(path.join(root, 'preview', 'index.html'), 'utf8');
const dom = new JSDOM(html, { url: 'http://localhost:8000/preview/index.html', runScripts: 'outside-only', pretendToBeVisual: true });
const ctx = { clearRect() {}, fillRect() {}, fillText() {}, beginPath() {}, arc() {}, fill() {},
  fillStyle: '', font: '' };
dom.window.HTMLCanvasElement.prototype.getContext = () => ctx;

globalThis.window = dom.window;
globalThis.document = dom.window.document;
globalThis.localStorage = dom.window.localStorage;
globalThis.setInterval = () => 1;
globalThis.clearInterval = () => {};
globalThis.fetch = async (url) => {
  const p = path.join(root, String(url).replace(/^\.\.\//, ''));
  return { ok: true, status: 200, json: async () => JSON.parse(fs.readFileSync(p, 'utf8')) };
};

const errors = [];
process.on('uncaughtException', (e) => errors.push(e));
process.on('unhandledRejection', (e) => errors.push(e));

const report = [];
let failed = 0;
const tempApp = path.join(root, 'preview', '_smoke.mjs');
fs.copyFileSync(path.join(root, 'preview', 'app.js'), tempApp);
try {
await import(pathToFileURL(tempApp).href + '?t=' + Date.now());
const $ = (id) => document.getElementById(id);
report.push(['boot: resources rendered', $('resources').children.length > 0]);
report.push(['boot: buildings rendered', $('buildings').innerHTML.includes('Stronghold')]);
report.push(['boot: units rendered', $('units').innerHTML.includes('Spearman')]);
report.push(['boot: nodes rendered', $('nodes').innerHTML.includes('Sunlit Mine')]);
report.push(['boot: browser checks ran', $('test-results').innerHTML.includes('PASS')]);

// Interact: run a battle manually, cast a skill, upgrade a building, switch race, explore a node.
$('deploy').click();
report.push(['deploy: battle created', $('tick-label').textContent.includes('T0')]);
const skillBtn = $('skills').querySelector('[data-skill]');
if (skillBtn) skillBtn.click();
report.push(['skill: targeting armed', $('targeting').textContent.includes('TARGETING')]);
$('seed').value = '7';
$('deploy').click();
for (let i = 0; i < 20; i++) {
  const btn = $('skills').querySelector('[data-skill]:not([disabled])');
  if (btn) btn.click();
  // simulate a grid click for casting at cell (9,3)
  $('grid').dispatchEvent(new dom.window.MouseEvent('click', { clientX: 9 * 60, clientY: 3 * 60, bubbles: true }));
  document.querySelector('[data-step]')?.click();
}
report.push(['battle: log populated', $('log').textContent.length > 20]);

const upgrade = $('buildings').querySelector('[data-upgrade]:not([disabled])');
if (upgrade) upgrade.click();
report.push(['economy: upgrade accepted', $('buildings').innerHTML.includes('upgrading') || $('economy-log').textContent.includes('Upgrade started')]);

document.querySelector('[data-race="lich"]')?.click();
report.push(['roster: race switch', $('race-summary').textContent.includes('summon') || $('race-summary').textContent.includes('Lich')]);

document.querySelector('[data-node="n01"]')?.click();
report.push(['world: exploration', $('economy-log').textContent.includes('Explored') || $('offline').textContent.length > 0]);

$('run-tests').click();
report.push(['tests: all passed', !$('test-results').innerHTML.includes('FAIL')]);

for (const [name, ok] of report) { console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}`); if (!ok) failed++; }
for (const e of errors) { console.log('RUNTIME ERROR', e.stack ?? e); failed++; }
} finally {
  fs.unlinkSync(tempApp);
}
console.log('\n' + (report.length - failed) + '/' + report.length + ' browser smoke checks passed.');
dom.window.close();
process.exitCode = failed ? 1 : 0;
