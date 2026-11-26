#!/usr/bin/env node
/**
 * Parse every C# file with tree-sitter to catch syntax errors without a .NET SDK.
 * Setup (once):  cd tools/.syntax && npm init -y && npm install --build-from-source tree-sitter@0.25.1 tree-sitter-c-sharp@0.24.0
 * Run:           node tools/syntax_check.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const syntaxDir = path.join(root, 'tools', '.syntax', 'node_modules');

async function loadParser() {
  if (!fs.existsSync(path.join(syntaxDir, 'tree-sitter', 'index.js')) ||
      !fs.existsSync(path.join(syntaxDir, 'tree-sitter-c-sharp', 'bindings', 'node', 'index.js'))) {
    console.error('Missing tree-sitter dependencies. Run:\n' +
      '  cd tools/.syntax && npm init -y && npm install --build-from-source tree-sitter@0.25.1 tree-sitter-c-sharp@0.24.0');
    process.exit(2);
  }
  const Parser = (await import(pathToFileURL(path.join(syntaxDir, 'tree-sitter', 'index.js')).href)).default;
  const CSharp = (await import(pathToFileURL(path.join(syntaxDir, 'tree-sitter-c-sharp', 'bindings', 'node', 'index.js')).href)).default;
  const parser = new Parser();
  parser.setLanguage(CSharp);
  return parser;
}

function* walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(p);
    else if (entry.name.endsWith('.cs')) yield p;
  }
}

function findError(node) {
  if (node.type === 'ERROR' || node.isMissing) return node;
  for (const child of node.children) {
    const found = findError(child);
    if (found) return found;
  }
  return null;
}

const parser = await loadParser();
const files = [...walk(path.join(root, 'Assets'))].sort();
let failed = 0;
for (const file of files) {
  const source = fs.readFileSync(file, 'utf8');
  const tree = parser.parse(source);
  const rel = path.relative(root, file);
  if (tree.rootNode.hasError) {
    const bad = findError(tree.rootNode);
    const line = bad ? bad.startPosition.row + 1 : 1;
    const snippet = source.split('\n')[line - 1] ?? '';
    console.error(`FAIL ${rel}:${line}  ${bad ? bad.type : 'parse error'}\n      ${snippet.trim()}`);
    failed++;
  } else {
    console.log(`ok   ${rel}  (${source.split('\n').length} lines)`);
  }
}
console.log(`\n${files.length - failed}/${files.length} C# files parsed cleanly.`);
process.exit(failed ? 1 : 0);
