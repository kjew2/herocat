#!/usr/bin/env node
// End-to-end browser check of the C# (wasm) backend: serves web/ over HTTP, loads index.html in headless Chromium at
// 390x844, plays ~10 s, visits every tab, summons x10, checks the Settings engine chip, reloads and checks the save restored.
// Fails (exit 1) on any page error, console error (Google Fonts aborts excluded) or failed assertion.
// Usage: node web/wasm/test/run-wasm.js [--keep-open] [--shots dir]
'use strict';
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { start } = require('./serve.js');

const WEB = path.resolve(__dirname, '..', '..');
const SAVE_KEY = 'herocat-wasm-save-v1';
const shotsDir = process.argv.includes('--shots') ? process.argv[process.argv.indexOf('--shots') + 1] : null;
const fails = [], errors = [];
const check = (ok, what) => { console.log((ok ? 'ok   ' : 'FAIL ') + what); if (!ok) fails.push(what); };

// Save wrapper written by engine.js: {v, clockOffsetMs, backend, state:"HEROCAT-SAVE ...\n{json}"}
function readSave(raw) {
  if (!raw) return null;
  const w = JSON.parse(raw);
  const body = JSON.parse(w.state.slice(w.state.indexOf('\n') + 1));
  return { wrapper: w, state: body.State, meta: body.Meta };
}

(async () => {
  if (!fs.existsSync(path.join(WEB, 'wasm', 'dist', '_framework', 'dotnet.js'))) { console.error('web/wasm/dist is missing: run web/wasm/build.sh first'); process.exit(2); }
  const srv = await start(WEB, 0);
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await ctx.route(/fonts\.(googleapis|gstatic)\.com/, (r) => r.abort());
  const page = await ctx.newPage();
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => {
    if (m.type() === 'error' && !/fonts\.g|ERR_FAILED|Failed to load resource/.test(m.text() + (m.location().url || ''))) errors.push('console.error: ' + m.text());
    if (m.type() === 'warning' && /wasm engine failed/.test(m.text())) errors.push('console.warn: ' + m.text());
  });
  const shot = async (n) => { if (shotsDir) { fs.mkdirSync(shotsDir, { recursive: true }); await page.screenshot({ path: path.join(shotsDir, n + '.png') }); } };
  const ready = () => page.waitForFunction(() => { const e = document.getElementById('stageEl'); return e && e.textContent && !/Loading|failed/.test(e.textContent) && document.querySelectorAll('#tabs button').length >= 6; }, null, { timeout: 90000 });
  const url = srv.url + 'index.html';

  // ---- cold load
  const t0 = Date.now();
  await page.goto(url);
  await ready();
  const coldMs = Date.now() - t0;
  console.log('cold start (navigation to playable UI): ' + coldMs + ' ms');
  await shot('01-fight');

  // ---- play ~10 s
  const hud0 = await page.evaluate(() => document.body.innerText.length);
  await page.waitForTimeout(10000);
  await page.evaluate(() => window.dispatchEvent(new Event('pagehide')));          // app.js saves on pagehide
  let save = readSave(await page.evaluate((k) => localStorage.getItem(k), SAVE_KEY));
  check(!!save, 'wasm save written to ' + SAVE_KEY);
  check(save && save.wrapper.backend === 'wasm', 'save wrapper tagged backend=wasm');
  check(save && save.state.Tick >= 80, 'sim advanced while playing (tick ' + (save && save.state.Tick) + ')');
  check(save && save.state.Lifetime.Kills > 0, 'kills happened (' + (save && save.state.Lifetime.Kills) + ')');

  // ---- tabs
  const tabNames = await page.$$eval('#tabs button', (bs) => bs.map((b) => b.textContent.trim()));
  check(tabNames.length >= 6, 'tabs: ' + tabNames.join(', '));
  for (const n of tabNames) {
    await page.click('#tabs button:has-text("' + n + '")');
    await page.waitForTimeout(400);
    await shot('tab-' + n.toLowerCase().replace(/\W/g, ''));
  }

  // ---- summon x10 (free banked pulls)
  await page.click('#tabs button:has-text("Summon")');
  await page.click('button:has-text("Summon x10")');
  await page.waitForSelector('#modal:not([hidden]) button:has-text("Skip")', { timeout: 10000 });
  await page.click('#modal button:has-text("Skip")');
  await page.waitForSelector('#modal button:has-text("Close")', { state: 'visible', timeout: 10000 });
  const summary = await page.$eval('#modal .muted', (e) => e.textContent);
  check(/pity/.test(summary), 'summon result shown: ' + summary);
  const cardName = await page.textContent('#modal .rcard .front b').catch(() => '');
  check(cardName && cardName.length > 0, 'hero card shows a name from the C# content: ' + cardName);
  await shot('summon-result');
  await page.click('#modal button:has-text("Close")');
  await page.waitForTimeout(300);
  await page.evaluate(() => window.dispatchEvent(new Event('pagehide')));
  save = readSave(await page.evaluate((k) => localStorage.getItem(k), SAVE_KEY));
  check(save.state.HeroBanner.TotalPulls === 10, 'save has 10 pulls (' + save.state.HeroBanner.TotalPulls + ')');
  const owned = Object.keys(save.state.Heroes.Copies).length;
  check(owned >= 1, 'heroes owned in save: ' + owned);

  // ---- Settings chip
  await page.click('#cogBtn');
  const chip = await page.$eval('#engineChip', (e) => e.textContent);
  check(/C# \(wasm\)/.test(chip) && /HeroCat\.Sim/.test(chip), 'Settings chip: ' + chip);
  const inv = await page.evaluate(() => document.querySelector('#modal').innerText.match(/Invariants: [^\n]*/)[0]);
  check(/Invariants: ok/.test(inv), inv);
  await shot('settings');
  await page.click('#modal button:has-text("Close")');
  const before = { tick: save.state.Tick, pulls: save.state.HeroBanner.TotalPulls, stage: save.state.Stage.Stage, gems: save.state.Currencies.Gems, copies: JSON.stringify(save.state.Heroes.Copies) };

  // ---- reload, restore
  await page.reload();
  await ready();
  await page.waitForTimeout(1500);
  await page.evaluate(() => window.dispatchEvent(new Event('pagehide')));
  const after = readSave(await page.evaluate((k) => localStorage.getItem(k), SAVE_KEY));
  check(after.state.HeroBanner.TotalPulls === before.pulls, 'pulls restored after reload (' + after.state.HeroBanner.TotalPulls + ')');
  check(JSON.stringify(after.state.Heroes.Copies) === before.copies, 'hero collection restored');
  check(after.state.Tick >= before.tick, 'tick continued (' + before.tick + ' -> ' + after.state.Tick + ')');
  check(after.state.Stage.Stage >= before.stage, 'stage kept (' + before.stage + ' -> ' + after.state.Stage.Stage + ')');
  check(after.state.Currencies.Gems >= before.gems - 0 && after.state.Currencies.Gems <= before.gems + 200, 'gems consistent (' + before.gems + ' -> ' + after.state.Currencies.Gems + ')');
  check(after.meta.SaveSequence > 0, 'meta.SaveSequence set (' + after.meta.SaveSequence + ')');
  await page.click('#cogBtn');
  const chip2 = await page.$eval('#engineChip', (e) => e.textContent);
  check(/C# \(wasm\)/.test(chip2), 'engine still wasm after reload: ' + chip2);
  await page.click('#modal button:has-text("Close")');
  await shot('after-reload');

  // ---- speed-up smoke (x100 for a few seconds exercises Advance with large batches)
  await page.click('#cogBtn');
  await page.click('#modal button:has-text("x100")');
  await page.click('#modal button:has-text("Close")');
  await page.waitForTimeout(4000);
  const inv2 = await page.evaluate(() => { document.getElementById('cogBtn').click(); const t = document.querySelector('#modal').innerText.match(/Invariants: [^\n]*/); return t && t[0]; });
  check(/Invariants: ok/.test(inv2 || ''), 'invariants after x100 run: ' + inv2);

  check(errors.length === 0, 'no page/console errors' + (errors.length ? ': ' + errors.slice(0, 5).join(' | ') : ''));

  // Opened as a file, the page must explain how to serve it instead of failing silently.
  const fp = await ctx.newPage();
  await fp.route(/fonts\.(googleapis|gstatic)/, (rt) => rt.abort());
  await fp.goto('file://' + require('path').resolve(__dirname, '../../index.html'));
  await fp.waitForSelector('.card.wall', { timeout: 10000 }).catch(() => {});
  const msg = await fp.textContent('.card.wall').catch(() => '');
  check(/over HTTP/.test(msg || ''), 'file:// shows how to serve the page');
  await browser.close();
  await srv.close();
  console.log(fails.length ? '\n' + fails.length + ' FAILED' : '\nall passed (cold start ' + coldMs + ' ms)');
  process.exit(fails.length ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
