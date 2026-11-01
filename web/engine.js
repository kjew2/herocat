// Engine facade over the C# core compiled to WebAssembly (src/HeroCat.Web, contract in web/wasm/BRIDGE.md).
// IdleEngine.create(opts) -> Promise<engine>. The engine exposes:
//   state (cached getter), content (UI labels and display numbers from the C# ContentDb), heroById,
//   every Query/Cmd name as a method, advance(n), drain(), emitHits (get/set), reload(opts), exportSave(), version().
// No game rule lives here: every value comes from the C# engine. Results are cached per state version so the
// per-frame UI path costs one State() call and almost no queries.
(function (root) {
  'use strict';

  const SAVE_KEY = 'herocat-wasm-save-v1';
  const WASM_BASE = 'wasm/dist/';

  const QUERIES = ['hp', 'bossHp', 'gold', 'statCost', 'statMult', 'gearTier', 'gearBasePct', 'gearEffPct', 'gearSlotMult', 'gearMult',
    'heroLevel', 'heroPct', 'heroMult', 'classMult', 'dps', 'hitDamage', 'hitsToKill', 'killTimeSeconds', 'isBossStage', 'bossClearable',
    'needDps', 'enhanceCost', 'enhanceSuccess', 'pityRate', 'freePullsRemaining', 'chestReadyAtMs', 'chestReady', 'bulkCost',
    'maxAffordable', 'pullPrice', 'bestParty', 'autoEquipWouldChange', 'nowMs', 'validate', 'consolidatedRates'];
  const COMMANDS = ['buyStatLevels', 'buyStatLevelsMax', 'enhance', 'attemptBoss', 'pull', 'equipHero', 'unequipHero', 'autoEquip',
    'claimChest', 'syncWallClock', 'debugGrantGold', 'debugGrantGems', 'debugSetStage'];

  // Caching classes: PURE depend only on content and arguments; BUILD change only when the player's build changes.
  const PURE = new Set(['hp', 'bossHp', 'gold', 'statCost', 'gearTier', 'gearBasePct', 'gearEffPct', 'heroLevel', 'pityRate',
    'enhanceSuccess', 'bulkCost', 'consolidatedRates']);
  const BUILD = new Set(['dps', 'hitDamage', 'heroMult', 'gearMult', 'gearSlotMult', 'hitsToKill', 'killTimeSeconds']);
  const BUILD_IF_NO_ARG = new Set(['statMult', 'classMult']);

  function randomSeedHex() {
    const a = new Uint32Array(2);
    try { root.crypto.getRandomValues(a); } catch (e) { a[0] = (Math.random() * 2 ** 32) >>> 0; a[1] = (Math.random() * 2 ** 32) >>> 0; }
    return a[0].toString(16).padStart(8, '0') + a[1].toString(16).padStart(8, '0');
  }
  function buildHeroById(content) {
    const out = {}, rar = content.rarities;
    content.heroes.pool.forEach((tierList) => tierList.forEach((h) => { out[h.id] = { id: h.id, name: h.name, short: h.short || h.name, tier: rar.indexOf(h.tier) }; }));
    return out;
  }

  // `host` is the .NET export object HeroCat.Web.Engine.
  function createEngine(o, host, contentPack) {
    const eng = { saveKey: SAVE_KEY, host: host };
    let cache = null, dirty = true, pendingEvents = true, version = 0, emit = false;
    let buildKey = '', pureMemo = new Map(), buildMemo = new Map(), versionMemo = new Map();
    const stats = { queries: 0, hits: 0, stateReads: 0 };
    eng.stats = stats;

    const touch = () => { dirty = true; pendingEvents = true; version++; versionMemo.clear(); };
    function refresh() {
      cache = JSON.parse(host.State());
      dirty = false; stats.stateReads++;
      const s = cache, g = s.gear, p = s.heroes.party, own = s.heroes.owned;
      let k = s.statLevel + '|' + s.classAdvances;
      for (let i = 0; i < g.length; i++) k += g[i] ? '|' + g[i].tier + ',' + g[i].rarity + ',' + g[i].enh : '|-';
      for (let i = 0; i < p.length; i++) k += p[i] ? '|' + p[i] + ':' + own[p[i]] : '|-';
      if (k !== buildKey) { buildKey = k; buildMemo.clear(); }
    }
    Object.defineProperty(eng, 'state', { get() { if (dirty) refresh(); return cache; } });
    Object.defineProperty(eng, 'stateVersion', { get() { return version; } });
    Object.defineProperty(eng, 'emitHits', { get() { return emit; }, set(v) { emit = !!v; host.SetEmitHits(emit ? 1 : 0); } });

    function wireArgs(a) {
      let n = a.length; while (n > 0 && a[n - 1] === undefined) n--;
      const out = new Array(n);
      for (let i = 0; i < n; i++) out[i] = a[i] === undefined ? null : a[i];
      return JSON.stringify(out);
    }
    const isErr = (v) => v && typeof v === 'object' && !Array.isArray(v) && typeof v.error === 'string' && !('ok' in v);
    function query(name, a) {
      const args = wireArgs(a);
      let memo;
      if (PURE.has(name)) memo = pureMemo;
      else if (BUILD.has(name)) { if (dirty) refresh(); memo = buildMemo; }
      else if (BUILD_IF_NO_ARG.has(name)) { if (args === '[]' || args === '[null]') { if (dirty) refresh(); memo = buildMemo; } else memo = pureMemo; }
      else memo = versionMemo;
      const key = name + args;
      if (memo.has(key)) { stats.hits++; return memo.get(key); }
      stats.queries++;
      let v = JSON.parse(host.Query(name, args));
      if (isErr(v)) { console.error('engine query ' + name + ' failed: ' + v.error); v = null; }
      if (memo !== pureMemo || pureMemo.size < 5000) memo.set(key, v);
      return v;
    }
    function cmd(name, a) {
      const r = JSON.parse(host.Cmd(name, wireArgs(a)));
      touch();
      if (isErr(r)) { console.error('engine command ' + name + ' failed: ' + r.error); return { ok: false, reason: 'engineError', error: r.error }; }
      return r === null ? undefined : r;
    }
    for (const n of QUERIES) eng[n] = function () { return query(n, arguments); };
    for (const n of COMMANDS) eng[n] = function () { return cmd(n, arguments); };

    eng.advance = function (n) { n = n | 0; if (n > 0) { host.Advance(n); touch(); } };
    eng.drain = function () {
      if (!pendingEvents) return [];
      pendingEvents = false;
      const s = host.Drain();
      return s && s !== '[]' ? JSON.parse(s) : [];
    };
    eng.exportSave = () => host.Export();
    eng.version = () => host.Version();
    eng.reload = function (opts) {
      opts = opts || {};
      let snapshot = null;
      if (cache || !dirty) { try { snapshot = host.Export(); } catch (e) { /* none yet */ } }
      const now = opts.nowMs !== undefined ? opts.nowMs : Date.now();
      const profileId = opts.profileId || 'web-' + Date.now().toString(36);
      try { host.Init(contentPack, opts.saveString || '', opts.profileSeedHex || randomSeedHex(), now, profileId); }
      catch (err) {
        if (snapshot) { try { host.Init(contentPack, snapshot, '0', now, profileId); } catch (e2) { /* nothing more to do */ } }
        touch(); throw err;
      }
      pureMemo = new Map(); buildMemo = new Map(); buildKey = '';
      if (emit) host.SetEmitHits(1);
      touch();
      eng.content = JSON.parse(host.UiContent());
      eng.heroById = buildHeroById(eng.content);
    };
    eng.reload(o);
    return eng;
  }

  function withTimeout(p, ms, what) {
    let t;
    return Promise.race([p, new Promise((_, rej) => { t = setTimeout(() => rej(new Error(what + ' timed out')), ms); })]).finally(() => clearTimeout(t));
  }
  // Loads the .NET runtime and the content pack. Rejects with a message the UI shows as-is.
  async function loadWasm(o) {
    const base = o.wasmBase || WASM_BASE;
    if (root.location && root.location.protocol === 'file:') {
      throw new Error('This page was opened as a file. Browsers only load the C# engine over HTTP: run `cd web && python3 -m http.server 8000` and open http://localhost:8000');
    }
    let head = null;
    try { head = await fetch(base + '_framework/dotnet.js', { method: 'HEAD' }); } catch (e) { /* network error */ }
    if (!head || !head.ok) throw new Error('The C# engine is not built. Run `web/wasm/build.sh`, then reload.');
    const abs = (p) => new URL(p, document.baseURI).href;
    const run = async () => {
      const { dotnet } = await import(abs(base + '_framework/dotnet.js'));
      const runtime = await dotnet.withDiagnosticTracing(false).create();
      const exports = await runtime.getAssemblyExports('HeroCat.Web.dll');
      const r = await fetch(base + 'content-pack.json');
      if (!r.ok) throw new Error('content-pack.json missing from ' + base + '. Rerun `web/wasm/build.sh`.');
      return { host: exports.HeroCat.Web.Engine, contentPack: await r.text() };
    };
    return withTimeout(run(), o.timeoutMs || 30000, 'Loading the C# engine');
  }

  // A save that does not load starts a new profile and reports why (engine.saveError).
  async function create(o) {
    o = o || {};
    const loaded = await loadWasm(o);
    try { return createEngine(o, loaded.host, loaded.contentPack); }
    catch (err) {
      if (!o.saveString) throw err;
      const e = createEngine(Object.assign({}, o, { saveString: '' }), loaded.host, loaded.contentPack);
      e.saveError = err.message;
      return e;
    }
  }

  // Save envelope: { v, backend: 'wasm', clockOffsetMs, state }. Saves from the removed JS engine are rejected.
  function wrapSave(eng, clockOffsetMs) {
    return JSON.stringify({ v: 1, backend: 'wasm', clockOffsetMs: clockOffsetMs || 0, state: eng.exportSave() });
  }
  function unwrapSave(str) {
    const w = JSON.parse(str);
    if (!w || typeof w !== 'object' || typeof w.state !== 'string') throw new Error('Not an HeroCat save');
    if (w.backend !== 'wasm') throw new Error('This save is from the old JS engine, which has been removed. Start a new profile.');
    return { clockOffsetMs: w.clockOffsetMs || 0, saveString: w.state };
  }

  root.IdleEngine = { create: create, wrapSave: wrapSave, unwrapSave: unwrapSave, SAVE_KEY: SAVE_KEY, QUERIES: QUERIES, COMMANDS: COMMANDS };
})(typeof self !== 'undefined' ? self : this);
