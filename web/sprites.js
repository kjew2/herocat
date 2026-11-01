// Seeded 12x12 pixel sprites drawn on canvas, no image files. Ported from the earlier web-proto build.
// Cosmetic only: uses its own hash-based RNG keyed by hero id / stage, never the game's RNG streams.
(function (root) {
  'use strict';
function hash(str) { let h = 2166136261; for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
const pick = (r, arr) => arr[Math.floor(r() * arr.length)];
const SPR = {};
const BODY = [
  "............",
  "............",
  "....SSSS....",
  "...SESSES...",
  "...SSSSSS...",
  "....SSSS....",
  "..AAAAAAAA..",
  "..SABBBBAS..",
  "..S.AAAA.S..",
  "....LLLL....",
  "....LL.LL...",
  "...FF...FF..",
];
const HATS = [
  ["....HHHH....","...HHHHHH...","...H....H..."],          // helm
  ["...HHHHHH...","..HHHHHHHH..","..HH....HH.."],          // hood
  [".....HH.....","...HHHHHH...","..HHHHHHHHH."],          // wizard hat
  ["....HHHH....","...HHHHHH...","............"],          // hair
  ["...H.HH.H...","...HHHHHH...","............"],          // crown
];
function heroSprite(id) {
  if (SPR[id]) return SPR[id];
  const h = { tier: id[0] }; const r = rng(hash(id + "hero"));
  const tierCol = { C: ["#8d8a9c","#b7b3c7"], R: ["#3d7fe0","#79b4ff"], E: ["#8b52e0","#c49bff"], L: ["#e98a1c","#ffc069"], M: ["#e0306f","#ff8ab3"] }[h.tier];
  const pal = {
    S: pick(r, ["#f5d0a9","#e0ac7e","#b97c51","#8a5a3b","#f2c6b0"]),
    E: "#1b1428",
    A: tierCol[0], B: tierCol[1],
    L: pick(r, ["#3b3355","#4a3b2a","#2e3d4f","#5a2e3e"]),
    F: "#2a2133",
    H: pick(r, ["#d9b84a","#6e4a2e","#c8c8d8","#7a2f3f","#2f6f5e","#3f3a8a","#e8e0c8","#9a3fd0"]),
    W: pick(r, ["#d6dbe6","#c9a45c","#8fd1ff"]),
  };
  if (h.tier === "L" || h.tier === "M") pal.H = h.tier === "M" ? "#ff8ab3" : "#ffd36b";
  const g = BODY.map(row => row.split(""));
  const hat = (h.tier === "L" || h.tier === "M") && r() < 0.6 ? HATS[4] : pick(r, HATS.slice(0, 4));
  hat.forEach((row, y) => row.split("").forEach((c, x) => { if (c === "H") g[y][x] = "H"; }));
  // hair/helm sides over the head
  if (hat !== HATS[2]) { g[2][3] = "H"; g[2][8] = "H"; }
  const weapon = Math.floor(r() * 3);
  if (weapon === 0) { for (let y = 3; y <= 8; y++) g[y][11] = "W"; g[7][10] = "W"; }           // sword
  else if (weapon === 1) { for (let y = 2; y <= 10; y++) g[y][10] = "L"; g[1][10] = "W"; g[2][11] = "W"; g[2][9] = "W"; } // staff
  else { g[4][10] = "W"; g[5][11] = "W"; g[6][11] = "W"; g[7][11] = "W"; g[8][10] = "W"; }      // bow
  return (SPR[id] = paint(g, pal));
}
function playerSprite(cls) {
  const key = "player" + cls; if (SPR[key]) return SPR[key];
  const cols = ["#9a8f7a","#a8b0c0","#c9ced8","#f2d27a","#ffe08a"];
  const pal = { S: "#e8c39e", E: "#1b1428", A: cols[cls], B: "#f2c14e", L: "#3b3355", F: "#2a2133", H: cols[cls], W: "#e6ebf5" };
  const g = BODY.map(row => row.split(""));
  HATS[0].forEach((row, y) => row.split("").forEach((c, x) => { if (c === "H") g[y][x] = "H"; }));
  g[2][3] = "H"; g[2][8] = "H";
  for (let y = 2; y <= 8; y++) g[y][11] = "W"; g[7][10] = "W"; g[6][10] = "B";
  if (cls >= 3) { g[0][5] = "B"; g[0][6] = "B"; }
  return (SPR[key] = paint(g, pal));
}
function enemySprite(seed, boss) {
  const key = "en" + seed + (boss ? "b" : ""); if (SPR[key]) return SPR[key];
  const r = rng(seed * 7919 + 13);
  const W = 12, Hh = 12; const g = Array.from({ length: Hh }, () => Array(W).fill("."));
  for (let y = 2; y < Hh; y++) for (let x = 0; x < W / 2; x++) {
    const cx = W / 2 - 0.5, dx = Math.abs(x - cx) / (W / 2), dy = Math.abs(y - 7) / 5.5;
    if (r() < 1.05 - (dx * dx + dy * dy) * 0.9) { g[y][x] = r() < 0.2 ? "B" : "A"; g[y][W - 1 - x] = g[y][x]; }
  }
  for (let y = 2; y < Hh; y++) { if (g[y][5] === ".") g[y][5] = "A"; if (g[y][6] === ".") g[y][6] = "A"; }
  const ey = 5 + Math.floor(r() * 2); g[ey][3] = "E"; g[ey][8] = "E"; g[ey][4] = "W"; g[ey][7] = "W";
  if (r() < .5) { g[ey + 2][4] = "E"; g[ey + 2][5] = "E"; g[ey + 2][6] = "E"; g[ey + 2][7] = "E"; }
  if (boss) { g[0][2] = "H"; g[1][2] = "H"; g[1][3] = "H"; g[0][9] = "H"; g[1][9] = "H"; g[1][8] = "H"; g[2][4] = "H"; g[2][7] = "H"; }
  const hue = Math.floor(r() * 360);
  const pal = { A: `hsl(${hue} 45% 45%)`, B: `hsl(${hue} 50% 32%)`, E: "#120d1c", W: "#fff4d6", H: boss ? "#f2c14e" : "#ddd" };
  return (SPR[key] = paint(g, pal));
}
function paint(g, pal) {
  const c = document.createElement("canvas"); c.width = g[0].length; c.height = g.length;
  const x = c.getContext("2d");
  g.forEach((row, yy) => row.forEach((ch, xx) => { if (ch !== "." && pal[ch]) { x.fillStyle = pal[ch]; x.fillRect(xx, yy, 1, 1); } }));
  return c;
}
  const ENEMY_NAMES = ["Slime","Goblin","Wolf","Skeleton","Bandit","Orc","Wraith","Golem","Harpy","Ogre","Lich","Drake","Wyrm","Titan"];
  function enemyName(stage, boss) { const n = ENEMY_NAMES[Math.floor(stage / 10) % ENEMY_NAMES.length]; return boss ? n + " Warlord" : n; }
  // Draws a sprite into a canvas element sized in CSS; the canvas keeps a 12x12 backing store.
  function draw(canvas, sprite) {
    if (canvas.width !== 12) { canvas.width = 12; canvas.height = 12; }
    const x = canvas.getContext("2d"); x.imageSmoothingEnabled = false; x.clearRect(0, 0, 12, 12); x.drawImage(sprite, 0, 0);
  }
  root.IdleSprites = {
    hero: heroSprite,
    player: playerSprite,
    enemy: function (stage, boss, variant) { return enemySprite(stage * 31 + (boss ? 999 : (variant || 0) % 4), boss); },
    enemyName: enemyName,
    draw: draw,
  };
})(typeof self !== 'undefined' ? self : this);
