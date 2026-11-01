#!/usr/bin/env node
// Bundles Content/manifest.json and every table file it lists into one JSON object
// { "manifest.json": "<raw text>", "<table>.json": "<raw text>", ... } for the wasm host (in-memory IContentSource).
// Raw text is preserved byte for byte, so the manifest sha256 checks (ContentDb.Load verifyHashes) pass unchanged.
// Usage: node web/wasm/pack-content.js [outFile ...]   (default: build/content-pack.json)
'use strict';
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..', '..');
const dir = path.join(root, 'Content');
const read = (f) => fs.readFileSync(path.join(dir, f), 'utf8').replace(/^﻿/, '');
const manifestText = read('manifest.json');
const manifest = JSON.parse(manifestText);
const pack = { 'manifest.json': manifestText };
for (const t of Object.keys(manifest.tables)) pack[manifest.tables[t].file] = read(manifest.tables[t].file);
const out = JSON.stringify(pack);
const outs = process.argv.slice(2);
if (!outs.length) outs.push(path.join(root, 'build', 'content-pack.json'));
for (const o of outs) { fs.mkdirSync(path.dirname(path.resolve(o)), { recursive: true }); fs.writeFileSync(o, out); }
console.log('content pack: ' + Object.keys(pack).length + ' files, ' + out.length + ' bytes -> ' + outs.join(', '));
