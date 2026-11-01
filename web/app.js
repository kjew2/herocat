// HeroCat browser prototype: the UI shell over the C# core compiled to WebAssembly (web/engine.js). No game rule
// lives here; it ticks the engine, sends it commands, reads its queries and drains its events (specs/07's split).
(function () {
  'use strict';
  const SP = window.IdleSprites;
  const IE = window.IdleEngine;
  const fmt = window.IdleFormat.formatShort;
  let C = null, R = null;                           // UI content from the C# engine (engine.content), set at boot
  const TAB_KEY = 'herocat-web-save-v1-tab';
  const MAX_TICKS_PER_FRAME = 2000;
  const $ = (id) => document.getElementById(id);
  const int = (n) => Math.floor(n).toLocaleString('en-US');
  const pct = (x, d) => (x * 100).toFixed(d === undefined ? 1 : d) + '%';

  // ------------------------------------------------------------ boot, clock, persistence
  // `sim` is the engine facade (web/engine.js) over the C# core. null until boot() finishes.
  let sim = null, clockOffsetMs = 0, speed = 1, bootNotice = null;
  const gameNow = () => Date.now() + clockOffsetMs;

  function newGame() {
    clockOffsetMs = 0;
    sim.reload({ nowMs: gameNow(), profileId: 'web-' + Date.now().toString(36) });   // random profile seed
    sim.emitHits = true;
  }
  function saveString() { return IE.wrapSave(sim, clockOffsetMs); }
  function loadString(str) {                       // throws (leaving the running game intact) on a bad or old-engine save
    const u = IE.unwrapSave(str);
    sim.reload({ saveString: u.saveString });
    clockOffsetMs = u.clockOffsetMs;
    sim.emitHits = true;
  }
  function save() { if (!sim) return; try { localStorage.setItem(sim.saveKey, saveString()); } catch (e) { /* storage blocked: play without saving */ } }
  function loadSaved() {
    try { const s = localStorage.getItem(sim.saveKey); if (s) { loadString(s); return true; } } catch (e) { console.warn('Save not loaded:', e); }
    return false;
  }

  // ------------------------------------------------------------ small DOM helper
  function h(tag, attrs, kids) {
    const el = document.createElement(tag);
    if (attrs) for (const k in attrs) {
      const v = attrs[k];
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) el.setAttribute(k, v === true ? '' : v);
    }
    if (kids) for (const kid of [].concat(kids)) if (kid !== null && kid !== undefined) el.append(kid);
    return el;
  }
  const setText = (el, s) => { if (el.textContent !== s) el.textContent = s; };
  const setHidden = (el, b) => { if (el.hidden !== b) el.hidden = b; };
  const setDisabled = (el, b) => { if (el.disabled !== b) el.disabled = b; };
  const toggle = (el, cls, b) => { if (el.classList.contains(cls) !== b) el.classList.toggle(cls, b); };

  function spriteCanvas(sprite, cls) {
    const cv = h('canvas', { class: 'sprite' + (cls ? ' ' + cls : ''), 'aria-hidden': 'true' });
    SP.draw(cv, sprite);
    return cv;
  }
  function heroGlyph(id, opts) {
    const hero = sim.heroById[id], owned = sim.state.heroes.owned[id] || 0;
    const el = h('button', { type: 'button', class: 'glyph r' + hero.tier + ' bg-r' + hero.tier, 'aria-label': hero.name + ' (' + id + ')', title: hero.name },
      [owned || (opts && opts.force) ? spriteCanvas(SP.hero(id)) : '?']);
    if (opts && opts.level !== false && owned) el.append(h('span', { class: 'lv', text: 'Lv' + sim.heroLevel(owned) }));
    return el;
  }

  // ------------------------------------------------------------ toasts and feed
  function toast(text, rarity, ms) {
    const box = $('toasts');
    while (box.children.length >= 3) box.firstChild.remove();
    const t = h('div', { class: 'toast' + (rarity !== undefined ? ' r' + rarity + ' bg-r' + rarity : ''), text: text });
    box.append(t);
    setTimeout(() => t.remove(), ms || 1800);
  }
  const feed = [];
  function log(text, cls) {
    feed.unshift({ text: text, cls: cls || '' });
    if (feed.length > 40) feed.pop();
    feedDirty = true;
  }
  let feedDirty = true;

  const pieceText = (p) => '[' + R[p.rarity] + '] T' + p.tier + (p.enh ? ' +' + p.enh : '');

  // ------------------------------------------------------------ events from the core
  let hitsThisFrame = 0;
  function handleEvents() {
    for (const e of sim.drain()) {
      switch (e.type) {
        case 'hit': hitsThisFrame++; break;
        case 'kill': if (speed <= 5) { enemyDeadUntil = performance.now() + 120; } break;
        case 'gearDropped': {
          const slot = C.gear.SLOTS[e.slot];
          if (e.equipped) {
            log(pieceText(e.piece) + ' ' + slot + ' equipped, +' + pct(sim.gearBasePct(e.piece), 0) + ' base', 'fg-r' + e.piece.rarity);
            toast('Equipped ' + pieceText(e.piece) + ' ' + slot + (e.replaced ? '  (was ' + pieceText(e.replaced) + ')' : ''), e.piece.rarity, [1500, 1500, 2500, 4000, 4000][e.piece.rarity]);
          } else {
            log(pieceText(e.piece) + ' ' + slot + ' discarded, not better than ' + pieceText(e.current), 'muted');
          }
          break;
        }
        case 'bossFailed': log('Boss failed on stage ' + e.stage + ' (attempt ' + e.attempt + ')', 'bad'); toast('Boss failed. Farming stage ' + e.stage + '.', undefined, 2000); break;
        case 'bossCleared': log('Boss cleared on stage ' + e.stage, 'ok'); break;
        case 'firstClear': log('+' + e.gems + ' gems first clear (stage ' + e.stage + ')', 'gems'); break;
        case 'questCompleted': log('Quest done: ' + e.label + '  +' + e.gems + ' gems', 'gems'); toast('Quest done: ' + e.label + '  +' + e.gems + ' gems'); break;
        case 'chestClaimed': log('Idle chest  +' + e.gems + ' gems', 'gems'); break;
        case 'dayRolledOver': log('New day: quests reset, ' + C.economy.FREE_PULLS_PER_DAY + ' free pulls', 'gems'); break;
        case 'classAdvanced': log('Class advance: ' + C.class.NAMES[e.advances] + ' (stage ' + e.stage + ')', 'ok'); showClassCard(e); break;
        case 'offlineGold': log('Offline: +' + fmt(e.gold) + ' gold for ' + hours(e.creditedS), 'gold'); showWelcomeBack(e); break;
        case 'enhanced': log((e.success ? 'Enhance success: ' : 'Enhance failed: ') + C.gear.SLOTS[e.slot] + ' +' + e.level, e.success ? 'ok' : 'muted'); break;
        case 'statLeveled': break;
        case 'pulled': break;
        case 'stageCap': log('Stage cap reached', 'bad'); break;
      }
    }
  }
  const needX = () => (Math.ceil(sim.needDps() / sim.dps() * 10) / 10).toFixed(1) + 'x';
  const hours = (s) => s >= 3600 ? (s / 3600).toFixed(1) + ' h' : Math.round(s / 60) + ' min';

  // ------------------------------------------------------------ main loop
  let lastFrame = performance.now(), tickAcc = 0, lastSync = 0, lastSave = performance.now(), lastRefresh = 0;
  let enemyDeadUntil = 0;
  function frame(now) {
    let dt = now - lastFrame; lastFrame = now;
    if (dt > 1000) { dt = 0; tickAcc = 0; sim.syncWallClock(gameNow()); }  // tab was asleep: catch-up pays for the gap
    if (speed > 1) clockOffsetMs += dt * (speed - 1);                         // speed-up moves the game clock too
    tickAcc += dt * C.combat.TICK_HZ * speed / 1000;
    let n = Math.floor(tickAcc);
    tickAcc -= n;
    if (n > MAX_TICKS_PER_FRAME) n = MAX_TICKS_PER_FRAME;                    // drop backlog (specs/07 section 6.3)
    if (n > 0) sim.advance(n);
    if (now - lastSync > 5000) { sim.syncWallClock(gameNow()); lastSync = now; }
    handleEvents();
    drawStrip(now);
    if (now - lastRefresh > 120) { refresh(); lastRefresh = now; }
    if (now - lastSave > 10000) { save(); lastSave = now; }
    requestAnimationFrame(frame);
  }

  // ------------------------------------------------------------ HUD + combat strip
  let lastGems = -1;
  function drawHud() {
    const s = sim.state;
    setText($('goldEl'), fmt(s.cur.gold));
    const gemsEl = $('gemsEl');
    setText(gemsEl, int(s.cur.gems));
    if (lastGems >= 0 && s.cur.gems > lastGems) { gemsEl.classList.remove('pulse'); void gemsEl.offsetWidth; gemsEl.classList.add('pulse'); }
    lastGems = s.cur.gems;
    const chest = $('chestBtn');
    const ready = sim.chestReady();
    toggle(chest, 'ready', ready);
    if (ready) setText(chest, 'Chest +' + C.economy.IDLE_CHEST_GEMS);
    else {
      const ms = sim.chestReadyAtMs() - sim.nowMs();
      const m = Math.max(0, Math.ceil(ms / 60000));
      setText(chest, 'Chest ' + (m >= 60 ? Math.floor(m / 60) + 'h' + String(m % 60).padStart(2, '0') + 'm' : m + 'm'));
    }
    const sc = $('speedChip');
    setHidden(sc, speed === 1);
    setText(sc, 'x' + speed);
  }

  function drawStrip(now) {
    const s = sim.state, cb = s.combat, st = s.stage, cc = C.combat;
    setText($('stageEl'), 'Stage ' + st.stage);
    setHidden($('bossTag'), !sim.isBossStage());
    const walled = st.boss === 'walled';
    setHidden($('wallTag'), !walled);
    const kills = $('killsEl');
    setText(kills, 'kills ' + st.killsInStage + '/' + cc.KILLS_PER_STAGE);
    toggle(kills, 'walled', walled);

    const enemy = $('enemyEl');
    const band = Math.floor(st.stage / 10), hue = (band * 37) % 360;
    const arena = enemy.parentNode;
    const bg = 'linear-gradient(hsl(' + hue + ' 35% 15%), hsl(' + ((hue + 30) % 360) + ' 30% 22%))';
    if (arena.dataset.band !== String(band)) { arena.style.background = bg; arena.dataset.band = String(band); }
    const isBossFight = cb.enemyIsBoss && cb.phase === 'fighting';
    const showBoss = isBossFight || (cb.phase === 'spawning' && st.boss === 'pending');
    const ekey = st.stage + '|' + showBoss + '|' + (s.life.kills % 4);
    const ecv = $('enemyCv');
    if (ecv.dataset.k !== ekey) { SP.draw(ecv, SP.enemy(st.stage, showBoss, s.life.kills)); ecv.dataset.k = ekey; }
    toggle(enemy, 'boss', showBoss);
    toggle(enemy, 'dead', cb.phase === 'spawning' || now < enemyDeadUntil);
    let label;
    if (cb.phase === 'spawning') label = st.boss === 'pending' ? 'Boss incoming' : SP.enemyName(st.stage, false);
    else label = SP.enemyName(st.stage, cb.enemyIsBoss);
    setText($('enemyLabel'), label);

    const hpFrac = cb.phase === 'fighting' ? Math.max(0, cb.enemyHp / cb.enemyMaxHp) : 0;
    $('hpFill').style.width = (hpFrac * 100).toFixed(1) + '%';
    setText($('hpText'), cb.phase === 'fighting' ? fmt(Math.max(0, cb.enemyHp)) + ' / ' + fmt(cb.enemyMaxHp) : '-');
    const showTimer = cb.phase === 'fighting' && cb.enemyIsBoss;
    setHidden($('timerBar'), !showTimer);
    if (showTimer) {
      $('timerFill').style.width = (cb.bossTimer / cc.BOSS_TIME_TICKS * 100).toFixed(1) + '%';
      setText($('timerText'), (cb.bossTimer / cc.TICK_HZ).toFixed(1) + ' s');
    }

    // hit feedback: one floater and party flash per frame at most, only at readable speeds
    if (hitsThisFrame && speed <= 5) {
      enemy.classList.add('hit');
      setTimeout(() => enemy.classList.remove('hit'), 80);
      const fl = $('floaters');
      if (fl.children.length < 6) {
        const f = h('span', { class: 'floater', text: fmt(sim.hitDamage()) });
        f.style.right = (20 + Math.random() * 40) + 'px';
        f.addEventListener('animationend', () => f.remove());
        fl.append(f);
      }
      for (const el of partyStripEls) if (el.classList.contains('filled')) { el.classList.add('flash'); setTimeout(() => el.classList.remove('flash'), 150); }
    }
    hitsThisFrame = 0;
  }

  let partyStripEls = [];
  function buildPartyStrip() {
    const box = $('partyStrip');
    box.textContent = '';
    partyStripEls = [];
    for (let i = 0; i < C.heroes.SLOTS; i++) {
      const el = h('div', { class: 'pslot' });
      partyStripEls.push(el); box.append(el);
    }
    box.addEventListener('click', () => selectTab('heroes'));
  }
  function drawStripSlow() {
    const party = sim.state.heroes.party;
    party.forEach((id, i) => {
      const el = partyStripEls[i];
      const t = id ? sim.heroById[id].tier : -1;
      const cls = 'pslot' + (id ? ' filled' : '') + (el.classList.contains('flash') ? ' flash' : '');
      if (el.className !== cls) el.className = cls;
      if ((el.dataset.id || '') !== (id || '')) {
        el.dataset.id = id || '';
        el.textContent = '';
        if (id) { el.append(spriteCanvas(SP.hero(id))); el.style.setProperty('--slot-col', 'var(--r-' + R[t] + ')'); el.title = sim.heroById[id].name; }
      }
    });
    const k = sim.state.classAdvances;
    const pc = $('playerCv');
    if (pc.dataset.k !== String(k)) { SP.draw(pc, SP.player(k)); pc.dataset.k = String(k); }
    setText($('dpsEl'), fmt(sim.dps()));
    const st = sim.state.stage;
    const hits = sim.hitsToKill(sim.hp(st.stage));
    setText($('hitInfo'), hits + (hits === 1 ? ' hit' : ' hits') + ' / ' + sim.killTimeSeconds(st.stage).toFixed(1) + ' s');
    const bb = $('bossBtn');
    const walled = st.boss === 'walled';
    setHidden(bb, !walled);
    if (walled) {
      const clear = sim.bossClearable();
      toggle(bb, 'clearable', clear);
      setText(bb, clear ? 'Boss: clearable' : 'Boss: need ~' + needX());
    }
  }
  $('bossBtn').addEventListener('click', () => { const r = sim.attemptBoss(); if (!r.ok) toast(r.reason === 'bossAlreadyPending' ? 'Boss is already coming' : 'No boss to fight'); });
  $('chestBtn').addEventListener('click', () => { if (!sim.claimChest().ok) toast('Chest not ready yet'); });
  $('cogBtn').addEventListener('click', () => showSettings());

  // ------------------------------------------------------------ tabs
  const TABS = [
    { id: 'fight', label: 'Fight' }, { id: 'stats', label: 'Stats' }, { id: 'gear', label: 'Gear' },
    { id: 'heroes', label: 'Heroes' }, { id: 'summon', label: 'Summon' }, { id: 'class', label: 'Class' },
  ];
  const tabEls = {}, tabBtns = {}, views = {};
  let currentTab = 'fight';
  function selectTab(id) {
    currentTab = id;
    for (const t of TABS) { tabEls[t.id].hidden = t.id !== id; tabBtns[t.id].setAttribute('aria-selected', String(t.id === id)); }
    try { localStorage.setItem(TAB_KEY, id); } catch (e) { /* ignore */ }
    $('panel').scrollTop = 0;
    refresh();
  }
  function buildTabs() {
    const nav = $('tabs'), panel = $('panel');
    for (const t of TABS) {
      const b = h('button', { type: 'button', role: 'tab', onclick: () => selectTab(t.id) }, [t.label]);
      tabBtns[t.id] = b; nav.append(b);
      const sec = h('section', { class: 'tab', role: 'tabpanel', 'aria-label': t.label });
      tabEls[t.id] = sec; panel.append(sec);
      views[t.id] = BUILD[t.id](sec);
    }
    let saved = 'fight';
    try { saved = localStorage.getItem(TAB_KEY) || 'fight'; } catch (e) { /* ignore */ }
    selectTab(views[saved] ? saved : 'fight');
  }
  function refresh() {
    drawHud();
    drawStripSlow();
    views[currentTab].update();
    // badges
    setBadge('heroes', sim.autoEquipWouldChange());
    setBadge('summon', sim.freePullsRemaining() > 0);
  }
  function setBadge(id, on) {
    const b = tabBtns[id];
    let d = b.querySelector('.dot');
    if (on && !d) b.append(h('span', { class: 'dot' }));
    if (!on && d) d.remove();
  }

  const BUILD = {};

  // ---- Fight
  BUILD.fight = function (root) {
    const wallTitle = h('h2', { class: 'bad' });
    const wallLine = h('div', { class: 'num' });
    const wallNeed = h('div', { class: 'muted' });
    const retry = h('button', { type: 'button', class: 'btn primary', onclick: () => sim.attemptBoss() }, ['Retry boss']);
    const wall = h('div', { class: 'card wall' }, [wallTitle, wallLine, wallNeed, retry]);
    const questRows = C.quests.map((q) => {
      const prog = h('span', { class: 'num muted' });
      const row = h('div', { class: 'quest' }, [h('span', { text: q.label }), prog, h('span', { class: 'q-reward gems num', text: '+' + q.gems })]);
      return { row: row, prog: prog };
    });
    const dayLeft = h('span', { class: 'muted' });
    const quests = h('div', { class: 'card' }, [h('div', { class: 'row between' }, [h('h3', { text: 'Daily quests' }), dayLeft])].concat(questRows.map((r) => r.row)));
    const list = h('ul', { class: 'feed' });
    const life = h('div', { class: 'life' });
    root.append(wall, quests, h('h3', { text: 'Recent' }), list, life);
    return {
      update() {
        const s = sim.state, st = s.stage;
        const walled = st.boss === 'walled';
        setHidden(wall, !walled);
        if (walled) {
          setText(wallTitle, 'Boss failed (attempt ' + st.bossFailsOnStage + ')');
          setText(wallLine, 'Boss HP ' + fmt(sim.bossHp(st.stage)) + '  ·  your DPS ' + fmt(sim.dps()) + '  ·  need ' + fmt(sim.needDps()));
          const clear = sim.bossClearable();
          setText(wallNeed, clear ? 'You can beat it now.' : 'Need about ' + needX() + ' DPS. Buy ATK levels, enhance gear or summon heroes. Farming stage ' + st.stage + ' mobs meanwhile.');
          setText(retry, clear ? 'Fight boss' : 'Retry boss');
        }
        C.quests.forEach((q, i) => {
          const done = s.daily.rewarded[i];
          toggle(questRows[i].row, 'done', done);
          setText(questRows[i].prog, Math.min(s.daily.progress[i], q.target) + '/' + q.target);
        });
        const msLeft = (s.daily.dayKey + 1) * 86400000 - sim.nowMs();
        setText(dayLeft, 'resets in ' + hours(Math.max(0, msLeft / 1000)));
        if (feedDirty) {
          list.textContent = '';
          for (const f of feed) list.append(h('li', { class: f.cls, text: f.text }));
          feedDirty = false;
        }
        const L = s.life;
        setText(life, 'Lifetime: kills ' + int(L.kills) + ' · bosses ' + L.bossKills + ' · fails ' + L.bossFails + ' · drops ' + L.gearDrops +
          ' · pulls ' + L.pulls + (L.firstWallStage ? ' · first wall: stage ' + L.firstWallStage : ''));
      },
    };
  };

  // ---- Stats
  BUILD.stats = function (root) {
    const level = h('div', { class: 'big' });
    const mult = h('div', { class: 'muted num' });
    const rows = C.economy.STAT_BULK_STEPS.concat(['max']).map((m) => {
      const btn = h('button', { type: 'button', class: 'btn' }, [m === 'max' ? 'Max' : 'x' + m]);
      const cost = h('span', { class: 'num gold' });
      const delta = h('span', { class: 'muted num' });
      btn.addEventListener('click', () => {
        const count = m === 'max' ? sim.maxAffordable() : m;
        const r = sim.buyStatLevels(count);
        if (r.ok) toast('ATK +' + count + ' level' + (count > 1 ? 's' : ''));
      });
      return { m: m, btn: btn, cost: cost, delta: delta, row: h('div', { class: 'stat-row' }, [btn, h('div', { class: 'stat-info' }, [cost, delta])]) };
    });
    root.append(
      h('div', { class: 'card' }, [h('h3', { text: 'Attack level' }), h('div', { class: 'row between' }, [level, mult])]),
      h('div', { class: 'card' }, rows.map((r) => r.row)),
      h('p', { class: 'muted', text: 'Each level adds ' + pct(C.economy.STAT_PER_LVL, 0) + ' of base ATK. Cost grows ' + pct(C.economy.STAT_COST_G - 1, 0) + ' per level, so this layer slows down while gear and heroes carry the curve.' }),
    );
    return {
      update() {
        const n = sim.state.statLevel, gold = sim.state.cur.gold;
        setText(level, 'Lv ' + n);
        setText(mult, 'ATK x' + sim.statMult().toFixed(2));
        for (const r of rows) {
          const m = r.m === 'max' ? sim.maxAffordable() : r.m;
          const cost = sim.bulkCost(n, Math.max(1, m));
          const can = m > 0 && gold >= cost;
          setDisabled(r.btn, !can);
          setText(r.btn, r.m === 'max' ? (m > 0 ? 'Max (' + m + ')' : 'Max') : 'x' + r.m);
          setText(r.cost, m > 0 ? 'Cost ' + fmt(cost) : 'Need ' + fmt(sim.statCost(n)));
          const to = n + Math.max(1, m);
          setText(r.delta, 'x' + sim.statMult(n).toFixed(2) + ' → x' + sim.statMult(to).toFixed(2) + ' · +' + pct(sim.statMult(to) / sim.statMult(n) - 1) + ' DPS');
        }
      },
    };
  };

  // ---- Gear
  BUILD.gear = function (root) {
    let selected = 0;
    const head = h('div', { class: 'row between' }, [h('h2', { text: 'Gear' }), h('span', { class: 'num muted' })]);
    const tiles = C.gear.SLOTS.map((name, i) => {
      const tag = h('span', { class: 'rtag' });
      const tier = h('span', { class: 'num' });
      const enh = h('span', { class: 'num muted' });
      const t = h('button', { type: 'button', class: 'tile', onclick: () => { selected = i; this_.update(); } },
        [h('span', { class: 'slotname', text: name }), h('div', { class: 'row' }, [tag, tier]), enh]);
      return { el: t, tag: tag, tier: tier, enh: enh };
    });
    const dTitle = h('h3');
    const dLine = h('div', { class: 'num' });
    const dHint = h('div', { class: 'muted' });
    const enhBtn = h('button', { type: 'button', class: 'btn primary', onclick: () => {
      const r = sim.enhance(selected);
      if (r.ok) toast(r.success ? 'Enhanced to +' + r.level : 'Enhance failed', r.success ? undefined : undefined, 1200);
    } });
    root.append(head, h('div', { class: 'gear-grid' }, tiles.map((t) => t.el)), h('div', { class: 'card' }, [dTitle, dLine, dHint, enhBtn]),
      h('p', { class: 'muted', text: 'Drops replace a slot when their base bonus is higher. Enhancement does not carry over to the new piece.' }));
    const this_ = {
      update() {
        setText(head.lastChild, 'x' + fmt(sim.gearMult()));
        tiles.forEach((t, i) => {
          const p = sim.state.gear[i];
          toggle(t.el, 'sel', i === selected);
          if (p) {
            t.tag.className = 'rtag r' + p.rarity + ' bg-r' + p.rarity;
            setText(t.tag, R[p.rarity]); setText(t.tier, 'T' + p.tier);
            setText(t.enh, p.enh >= C.gear.ENH_MAX ? '+' + p.enh + ' MAX' : '+' + p.enh);
          } else { t.tag.className = 'rtag'; setText(t.tag, ''); setText(t.tier, ''); setText(t.enh, 'empty'); }
        });
        const p = sim.state.gear[selected];
        const name = C.gear.SLOTS[selected];
        if (!p) {
          setText(dTitle, name); setText(dLine, 'Empty. Pieces drop from kills (' + pct(C.gear.DROP_P, 1) + ') and bosses (' + pct(C.gear.BOSS_DROP_P, 0) + ').');
          setText(dHint, ''); setText(enhBtn, 'Empty slot'); setDisabled(enhBtn, true); return;
        }
        setText(dTitle, name + '  ' + C.rarityNames[R[p.rarity]] + ' T' + p.tier + ' +' + p.enh);
        setText(dLine, 'Base +' + pct(sim.gearBasePct(p), 0) + ' · Effective +' + pct(sim.gearEffPct(p), 0) + ' · Slot x' + fmt(sim.gearSlotMult(selected)));
        setText(dHint, p.tier === 0 ? 'Tier 0: enhancing adds nothing yet.' : '');
        const cost = sim.enhanceCost();
        if (p.enh >= C.gear.ENH_MAX) { setText(enhBtn, 'Max level'); setDisabled(enhBtn, true); }
        else if (sim.state.cur.gold < cost) { setText(enhBtn, 'Need ' + fmt(cost) + ' gold'); setDisabled(enhBtn, true); }
        else { setText(enhBtn, 'Enhance +' + p.enh + ' → +' + (p.enh + 1) +  ' · ' + pct(sim.enhanceSuccess(p.enh), 0) +  ' · ' + fmt(cost) + ' gold'); setDisabled(enhBtn, false); }
      },
    };
    return this_;
  };

  // ---- Heroes
  BUILD.heroes = function (root) {
    let selected = null;
    const multEl = h('span', { class: 'num muted' });
    const auto = h('button', { type: 'button', class: 'btn small', onclick: () => { const n = sim.autoEquip(); toast(n ? 'Changed ' + n + ' slot' + (n > 1 ? 's' : '') : 'Party is already the best six'); rebuild(); } }, ['Auto-equip best']);
    const partyRow = h('div', { class: 'party-row' });
    const grid = h('div', { class: 'hero-grid' });
    const dName = h('h2');
    const dLine = h('div', { class: 'num' });
    const dSprite = h('canvas', { class: 'sprite', 'aria-hidden': 'true' });
    const addBtn = h('button', { type: 'button', class: 'btn small', onclick: () => {
      const slot = sim.state.heroes.party.indexOf(null);
      if (slot >= 0 && selected) { sim.equipHero(slot, selected); rebuild(); }
    } }, ['Add to party']);
    const remBtn = h('button', { type: 'button', class: 'btn small', onclick: () => {
      const slot = sim.state.heroes.party.indexOf(selected);
      if (slot >= 0) { sim.unequipHero(slot); rebuild(); }
    } }, ['Remove']);
    const detail = h('div', { class: 'card' }, [h('div', { class: 'hero-head' }, [dSprite, h('div', null, [dName, dLine])]), h('div', { class: 'row' }, [addBtn, remBtn])]);
    const countEl = h('span', { class: 'muted' });
    root.append(
      h('div', { class: 'row between' }, [h('h2', { text: 'Heroes' }), multEl]),
      h('div', { class: 'card' }, [h('div', { class: 'row between' }, [h('h3', { text: 'Party' }), auto]), partyRow,
        h('div', { class: 'muted', text: 'Tap a hero below, then a party slot to place it. Tap a slot with nothing selected to empty it.' })]),
      detail,
      h('div', { class: 'row between' }, [h('h3', { text: 'Collection' }), countEl]),
      grid,
    );
    let sig = '';
    function rebuild() {
      sig = '';
      this_.update();
    }
    const this_ = {
      update() {
        const s = sim.state;
        setText(multEl, 'x' + sim.heroMult().toFixed(2));
        setDisabled(auto, !sim.autoEquipWouldChange());
        const newSig = JSON.stringify(s.heroes) + '|' + selected;
        if (newSig !== sig) {
          sig = newSig;
          partyRow.textContent = '';
          s.heroes.party.forEach((id, i) => {
            const el = id ? heroGlyph(id) : h('button', { type: 'button', class: 'empty-slot', text: 'empty' });
            el.addEventListener('click', () => {
              if (selected && s.heroes.owned[selected]) {
                const at = s.heroes.party.indexOf(selected);
                if (at >= 0 && at !== i) sim.unequipHero(at);
                sim.equipHero(i, selected);
              } else if (id) sim.unequipHero(i);
              rebuild();
            });
            partyRow.append(el);
          });
          grid.textContent = '';
          let owned = 0;
          for (const tier of C.heroes.pool) for (const hero of tier) {
            const copies = s.heroes.owned[hero.id] || 0;
            if (copies) owned++;
            const g = heroGlyph(hero.id);
            if (!copies) g.classList.add('unowned');
            if (s.heroes.party.indexOf(hero.id) >= 0) g.append(h('span', { class: 'inparty' }));
            if (selected === hero.id) g.classList.add('sel');
            g.addEventListener('click', () => { selected = copies ? (selected === hero.id ? null : hero.id) : null; if (!copies) toast(hero.name + ' (' + C.rarityNames[hero.tier] + ') not summoned yet'); rebuild(); });
            grid.append(g);
          }
          setText(countEl, owned + ' / ' + Object.keys(sim.heroById).length + ' owned');
        }
        if (!selected) {
          setHidden(dSprite, true);
          setText(dName, 'No hero selected'); setText(dLine, 'Party bonus +' + pct(sim.heroMult() - 1, 0) + ' DPS from ' + s.heroes.party.filter(Boolean).length + ' heroes.');
          setHidden(addBtn, true); setHidden(remBtn, true); return;
        }
        const hero = sim.heroById[selected], copies = s.heroes.owned[selected], lvl = sim.heroLevel(copies);
        const cum = C.heroes.LVL_CUM;
        setHidden(dSprite, false);
        if (dSprite.dataset.id !== selected) { SP.draw(dSprite, SP.hero(selected)); dSprite.dataset.id = selected; dSprite.className = 'sprite bg-r' + hero.tier; }
        setText(dName, hero.name + ' · ' + C.rarityNames[R[hero.tier]] + ' ' + selected);
        setText(dLine, 'Lv' + lvl + (lvl >= cum.length ? ' MAX' : ' (' + copies + '/' + cum[lvl] + ' copies)') + ' · +' + sim.heroPct(selected).toFixed(0) + '% DPS');
        const inParty = s.heroes.party.indexOf(selected) >= 0;
        setHidden(addBtn, inParty); setHidden(remBtn, !inParty);
        setDisabled(addBtn, s.heroes.party.indexOf(null) < 0);
        setText(addBtn, s.heroes.party.indexOf(null) < 0 ? 'Party full: tap a slot' : 'Add to party');
      },
    };
    return this_;
  };

  // ---- Summon
  BUILD.summon = function (root) {
    const pityText = h('span', { class: 'num' });
    const fill = h('div', { class: 'fill' });
    const mark = h('div', { class: 'mark' });
    mark.style.left = (C.banner.PITY_SOFT_START / C.banner.PITY_HARD * 100) + '%';
    const totals = h('div', { class: 'muted' });
    const p1Label = h('small'), p10Label = h('small');
    const p1 = h('button', { type: 'button', class: 'btn gemsy', onclick: () => doPull(1) }, [h('span', { text: 'Summon x1' }), p1Label]);
    const p10 = h('button', { type: 'button', class: 'btn gemsy', onclick: () => doPull(10) }, [h('span', { text: 'Summon x10' }), p10Label]);
    const last = h('div', { class: 'last' });
    root.append(
      h('div', { class: 'row between' }, [h('h2', { text: 'Hero banner' }), h('button', { type: 'button', class: 'btn small', onclick: showRates }, ['Rates'])]),
      h('div', { class: 'card' }, [
        h('div', { class: 'row between' }, [pityText, h('span', { class: 'muted', text: 'chance rises after pull ' + C.banner.PITY_SOFT_START })]),
        h('div', { class: 'pity' }, [fill, mark]), totals]),
      h('div', { class: 'pull-btns' }, [p1, p10]),
      h('p', { class: 'muted', text: 'x10 guarantees at least one Epic or better. Free daily pulls only pay for single summons.' }),
      h('h3', { text: 'Last summon' }), last,
    );
    let lastSig = '';
    return {
      update() {
        const s = sim.state;
        setText(pityText, 'Pity ' + s.banner.pity + ' / ' + C.banner.PITY_HARD);
        fill.style.width = (s.banner.pity / C.banner.PITY_HARD * 100) + '%';
        setText(totals, 'Total summons ' + s.banner.totalPulls + ' · Free today ' + s.cur.freePullsDaily + '/' + C.economy.FREE_PULLS_PER_DAY +
          (s.cur.freePullsBanked ? ' · Banked free ' + s.cur.freePullsBanked : ''));
        const pr1 = sim.pullPrice(1), pr10 = sim.pullPrice(10);
        setDisabled(p1, !pr1 || revealing); setDisabled(p10, !pr10 || revealing);
        setText(p1Label, !pr1 ? int(C.economy.PULL_COST_GEMS) + ' gems' : pr1.with === 'gems' ? int(pr1.gems) + ' gems' : 'Free (' + sim.freePullsRemaining() + ' left)');
        setText(p10Label, pr10 && pr10.with === 'freeBanked' ? '10 free pulls' : int(10 * C.economy.PULL_COST_GEMS) + ' gems');
        const sig = lastResults.map((r) => r.id).join(',');
        if (sig !== lastSig) {
          lastSig = sig;
          last.textContent = '';
          for (const r of lastResults) last.append(heroGlyph(r.id, { level: false, force: true }));
        }
      },
    };
  };
  let lastResults = [], revealing = false;

  function doPull(count) {
    if (revealing) return;
    const r = sim.pull(count);
    if (!r.ok) { toast('Not enough gems'); return; }
    lastResults = r.results;
    showReveal(r.results, count);
  }

  function showReveal(results, count) {
    revealing = true;
    const grid = h('div', { class: 'reveal-grid' + (count === 1 ? ' one' : '') });
    let t = 0.3;
    results.forEach((r, i) => {
      const lplus = r.tier >= 3;
      if (lplus) t += 0.4;
      const card = h('div', { class: 'rcard' + (lplus ? ' glow' : '') });
      card.style.setProperty('--d', t.toFixed(2) + 's');
      const sub = r.isNew ? 'NEW' : r.levelAfter > r.levelBefore ? 'Lv ' + r.levelBefore + ' → ' + r.levelAfter : 'Lv ' + r.levelAfter;
      card.append(
        h('div', { class: 'face back', text: '?' }),
        h('div', { class: 'face front r' + r.tier + ' bg-r' + r.tier }, [spriteCanvas(SP.hero(r.id)), h('b', { text: sim.heroById[r.id].short }), h('small', { text: sub + (r.shards ? ' +' + r.shards + ' shards' : '') })]),
      );
      grid.append(card);
      t += count === 1 ? 0 : 0.15;
    });
    const total = (t + 0.6) * 1000;
    const counts = [0, 0, 0, 0, 0];
    results.forEach((r) => counts[r.tier]++);
    const summary = h('div', { class: 'muted', text: counts.map((n, i) => n ? n + ' ' + C.rarityNames[R[i]] : '').filter(Boolean).reverse().join(', ') + ' · pity ' + sim.state.banner.pity + '/' + C.banner.PITY_HARD });
    summary.hidden = true;
    const again = h('button', { type: 'button', class: 'btn gemsy', onclick: () => { closeModal(); doPull(count); } }, ['Summon x' + count + ' again']);
    const close = h('button', { type: 'button', class: 'btn', onclick: closeModal }, ['Close']);
    const skip = h('button', { type: 'button', class: 'btn small', onclick: finish }, ['Skip']);
    const actions = h('div', { class: 'row' }, [again, close]);
    actions.hidden = true;
    function finish() {
      grid.classList.add('skip'); summary.hidden = false; actions.hidden = false; skip.hidden = true;
      setDisabled(again, !sim.pullPrice(count));
    }
    const timer = setTimeout(finish, total);
    openModal(h('div', { class: 'card' }, [h('div', { class: 'row between' }, [h('h2', { text: count === 1 ? 'Summon' : 'Summon x10' }), skip]), grid, summary, actions]), () => { clearTimeout(timer); revealing = false; });
    if (matchMedia('(prefers-reduced-motion: reduce)').matches) finish();
    if (sim.autoEquipWouldChange() && sim.state.heroes.party.indexOf(null) >= 0) sim.autoEquip();  // fill empty party slots
  }

  // ---- Class
  BUILD['class'] = function (root) {
    const name = h('h2');
    const mult = h('span', { class: 'num muted' });
    const next = h('div', { class: 'num' });
    const barFill = h('div', { class: 'fill' });
    barFill.style.background = 'var(--ok)';
    const rows = C.class.STAGES.map((stage, i) => {
      const row = h('div', { class: 'ms' }, [h('span', { class: 'chk' }), h('span', { class: 'num', text: 'Stage ' + stage }),
        h('span', { text: C.class.NAMES[i + 1] }), h('span', { class: 'num muted', text: 'ATK x' + sim.classMult(i + 1).toFixed(2) })]);
      return row;
    });
    root.append(
      h('div', { class: 'card' }, [h('div', { class: 'row between' }, [name, mult]), next, h('div', { class: 'bar' }, [barFill, h('span')])]),
      h('div', { class: 'card' }, rows),
      h('p', { class: 'muted', text: 'Class advances happen automatically when you reach the stage. Each one multiplies ATK by ' + C.class.MULT + '.' }),
    );
    return {
      update() {
        const k = sim.state.classAdvances, stage = sim.state.stage.stage, st = C.class.STAGES;
        setText(name, C.class.NAMES[k]);
        setText(mult, 'ATK x' + sim.classMult().toFixed(2));
        rows.forEach((r, i) => toggle(r, 'got', i < k));
        if (k >= st.length) { setText(next, 'All class advances done'); barFill.style.width = '100%'; return; }
        const prev = k === 0 ? 1 : st[k - 1];
        setText(next, 'Next: ' + C.class.NAMES[k + 1] + ' at stage ' + st[k] + ' · (' + stage + '/' + st[k] + ')');
        barFill.style.width = Math.max(0, Math.min(1, (stage - prev) / (st[k] - prev))) * 100 + '%';
      },
    };
  };

  // ------------------------------------------------------------ modal layer
  let onModalClose = null;
  function openModal(node, onClose) {
    closeModal();
    const m = $('modal');
    m.textContent = ''; m.append(node); m.hidden = false; m.scrollTop = 0;
    onModalClose = onClose || null;
  }
  function closeModal() {
    const m = $('modal');
    if (m.hidden) return;
    m.hidden = true; m.textContent = '';
    const f = onModalClose; onModalClose = null;
    if (f) f();
  }
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });

  function showRates() {
    const cr = sim.consolidatedRates();
    const b = C.banner;
    const rows = R.map((r, i) => h('tr', null, [h('td', { class: 'fg-r' + i, text: C.rarityNames[r] }), h('td', { class: 'num', text: pct(b.BASE_RATES[i], 2) }),
      h('td', { class: 'num', text: i === 3 ? pct(cr.l, 3) : i === 4 ? pct(cr.m, 3) : '-' })]));
    const ramp = [50, 55, 60, 65, 70, 75, 80].map((n) => 'pull ' + n + ': ' + pct(sim.pityRate(n), 1)).join( ' · ');
    openModal(h('div', { class: 'card' }, [
      h('div', { class: 'row between' }, [h('h2', { text: 'Rates' }), h('button', { type: 'button', class: 'btn small', onclick: closeModal }, ['Close'])]),
      h('div', { style: 'overflow-x:auto' }, h('table', { class: 'rates' }, [h('tr', null, [h('th', { text: 'Tier' }), h('th', { text: 'Base' }), h('th', { text: 'With pity' })])].concat(rows))),
      h('p', { text: 'Legendary or better: base ' + pct(b.BASE_LPLUS, 2) + ', with pity ' + pct(cr.lplus, 3) + '. Expected pulls per L+: ' + cr.expectedPulls.toFixed(1) + '.' }),
      h('p', { class: 'muted', text: 'From pull ' + (b.PITY_SOFT_START + 1) + ' the L+ chance rises ' + (b.PITY_SOFT_STEP * 100).toFixed(2) + ' points per pull and is 100% at pull ' + b.PITY_HARD + '. The counter resets on any Legendary or Mythic.' }),
      h('p', { class: 'muted num', text: ramp }),
      h('p', { class: 'muted', text: 'x10: if slots 1-9 are all below Epic, slot 10 is Epic or better.' }),
    ]));
  }

  function showWelcomeBack(e) {
    if (e.creditedS < 60) return;
    const capped = e.elapsedMs / 1000 > e.creditedS;
    openModal(h('div', { class: 'card' }, [
      h('h2', { text: 'Welcome back' }),
      h('p', { text: 'You were away ' + hours(e.elapsedMs / 1000) + '.' + (capped ? ' Offline earnings stop after ' + C.economy.OFFLINE_CAP_H + ' h.' : '') }),
      h('div', { class: 'big gold', text: '+' + fmt(e.gold) + ' gold' }),
      h('p', { class: 'muted', text: 'Offline you earn ' + pct(C.economy.OFFLINE_RATE, 0) + ' of your in-app rate at your current stage.' }),
      h('button', { type: 'button', class: 'btn primary', onclick: closeModal }, ['Collect']),
    ]));
  }

  let classCardTimer = 0;
  function showClassCard(e) {
    openModal(h('div', { class: 'card' }, [
      h('h3', { text: 'Class advance' }),
      h('div', { class: 'big', text: C.class.NAMES[e.advances] }),
      h('p', { class: 'num', text: 'ATK x' + C.class.MULT + ' · DPS ' + fmt(e.dpsBefore) + ' → ' + fmt(e.dpsAfter) }),
      h('button', { type: 'button', class: 'btn primary', onclick: closeModal }, ['OK']),
    ]), () => clearTimeout(classCardTimer));
    clearTimeout(classCardTimer);
    classCardTimer = setTimeout(closeModal, 6000);
  }

  // ---- Settings + debug (specs/07 section 11.8)
  function engineLine() {
    return h('div', { class: 'muted num' }, [h('span', { id: 'engineChip', text: 'Engine: C# (wasm) · ' + sim.version() })]);
  }
  function showSettings() {
    let resetArmed = 0;
    const speedBtns = [1, 5, 20, 100].map((x) => h('button', { type: 'button', class: 'btn small', 'aria-pressed': String(speed === x), onclick: () => { speed = x; showSettings(); } }, ['x' + x]));
    const skip = (ms, label) => h('button', { type: 'button', class: 'btn small', onclick: () => {
      clockOffsetMs += ms; closeModal(); sim.syncWallClock(gameNow()); handleEvents(); toast('Clock moved ' + label);
    } }, ['+' + label]);
    const stageIn = h('input', { type: 'number', id: 'dbgStage', min: '1', value: String(sim.state.stage.stage) });
    const area = h('textarea', { id: 'dbgSave', spellcheck: 'false', 'aria-label': 'Save data' });
    const status = h('div', { class: 'muted' });
    const resetBtn = h('button', { type: 'button', class: 'btn small', onclick: () => {
      if (Date.now() - resetArmed < 3000) { newGame(); save(); feed.length = 0; log('New profile started'); closeModal(); buildPartyStrip(); refresh(); return; }
      resetArmed = Date.now(); resetBtn.textContent = 'Tap again to reset';
      setTimeout(() => { resetBtn.textContent = 'Reset profile'; }, 3000);
    } }, ['Reset profile']);
    const errs = sim.validate();
    openModal(h('div', { class: 'card' }, [
      h('div', { class: 'row between' }, [h('h2', { text: 'Settings' }), h('button', { type: 'button', class: 'btn small', onclick: closeModal }, ['Close'])]),
      h('div', { class: 'muted num', text: 'Content ' + C.version + ' · save schema v' + sim.state.schema + ' · profile ' + sim.state.profileId }),
      engineLine(),
      h('div', { class: 'muted num', text: 'Game clock ' + new Date(gameNow()).toISOString().replace('T', ' ').slice(0, 16) + ' UTC' + (clockOffsetMs ? ' (ahead ' + hours(clockOffsetMs / 1000) + ')' : '') }),
      h('div', { class: 'muted', text: 'Invariants: ' + (errs.length ? errs.join('; ') : 'ok') }),
      resetBtn,
      h('h3', { text: 'Playtest tools' }),
      h('div', { class: 'dbg seg' }, [h('label', { text: 'Speed' })].concat(speedBtns)),
      h('div', { class: 'muted', text: 'Speed-up also moves the game clock, so chests, offline time and day resets come faster.' }),
      h('div', { class: 'dbg' }, [h('label', { text: 'Clock' }), skip(3600000, '1 h'), skip(8 * 3600000, '8 h'), skip(86400000, '1 day')]),
      h('div', { class: 'dbg' }, [h('label', { text: 'Gold' }),
        h('button', { type: 'button', class: 'btn small', onclick: () => sim.debugGrantGold(sim.statCost(sim.state.statLevel) * 10) }, ['+10 levels worth']),
        h('button', { type: 'button', class: 'btn small', onclick: () => sim.debugGrantGold(sim.enhanceCost() * 10) }, ['+10 enhances'])]),
      h('div', { class: 'dbg' }, [h('label', { text: 'Gems' }),
        h('button', { type: 'button', class: 'btn small', onclick: () => sim.debugGrantGems(1000) }, ['+1,000']),
        h('button', { type: 'button', class: 'btn small', onclick: () => sim.debugGrantGems(10000) }, ['+10,000'])]),
      h('div', { class: 'dbg' }, [h('label', { text: 'Stage' }), stageIn,
        h('button', { type: 'button', class: 'btn small', onclick: () => { sim.debugSetStage(parseInt(stageIn.value, 10) || 1); handleEvents(); toast('Stage set'); } }, ['Set'])]),
      h('h3', { text: 'Save' }),
      h('div', { class: 'dbg' }, [
        h('button', { type: 'button', class: 'btn small', onclick: () => {
          area.value = saveString();
          area.select();
          if (navigator.clipboard) navigator.clipboard.writeText(area.value).then(() => setText(status, 'Copied to clipboard.'), () => setText(status, 'Selected. Copy it manually.'));
        } }, ['Export']),
        h('button', { type: 'button', class: 'btn small', onclick: () => {
          try { loadString(area.value.trim()); save(); feed.length = 0; log('Save imported'); closeModal(); buildPartyStrip(); refresh(); toast('Save imported'); }
          catch (err) { setText(status, 'Import failed: ' + err.message); }
        } }, ['Import']),
      ]),
      area, status,
    ]));
  }

  // ------------------------------------------------------------ lifecycle
  function start(preloaded) {
    if (!preloaded && !loadSaved()) newGame();
    lastFrame = performance.now();
    sim.syncWallClock(gameNow());     // catch-up for time spent away
    buildPartyStrip();
    buildTabs();
    handleEvents();
    if (bootNotice) toast(bootNotice, undefined, 4500);
    if (sim.state.life.kills === 0 && sim.state.banner.totalPulls === 0) {
      log('Welcome. You have ' + int(sim.state.cur.gems) + ' gems and ' + sim.state.cur.freePullsBanked + ' free summons: open Summon for your first x10.', 'gems');
    }
    refresh();
    save();
    requestAnimationFrame(frame);
  }
  document.addEventListener('visibilitychange', () => {
    if (!sim) return;
    if (document.hidden) save();
    else { lastFrame = performance.now(); sim.syncWallClock(gameNow()); handleEvents(); }
  });
  window.addEventListener('pagehide', save);

  // Keep the game running across artifact republishes (no-op outside the claude.ai viewer).
  const hot = window.claude && window.claude.hot;
  if (hot && hot.snapshot) hot.snapshot(() => (sim ? { save: saveString() } : {}));
  async function boot(data) {
    // First paint: the HUD and strip exist in the HTML; say what we are waiting for.
    setText($('stageEl'), 'Loading engine...'); setText($('hpText'), 'Loading engine...');
    try {
      sim = await IE.create({ nowMs: gameNow(), profileId: 'web-' + Date.now().toString(36) });
    } catch (err) {
      const msg = String(err && err.message || err);
      setText($('stageEl'), 'Engine failed to load'); setText($('hpText'), '-');
      $('panel').append(h('div', { class: 'card wall' }, [h('h2', { class: 'bad', text: 'The C# engine did not load' }), h('p', { text: msg }),
        h('p', { class: 'muted', text: 'Setup steps are in the README under "Running the web prototype".' })]));
      throw err;
    }
    C = sim.content; R = C.rarities;
    if (sim.saveError) bootNotice = 'Save could not be loaded: ' + sim.saveError;
    sim.emitHits = true;
    let preloaded = false;
    if (data && data.save) { try { loadString(data.save); preloaded = true; } catch (e) { /* bad save: fall back to storage */ } }
    start(preloaded);
  }
  if (hot && hot.ready) hot.ready(boot); else boot(hot && hot.data || {});
})();
