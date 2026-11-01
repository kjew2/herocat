// Display formatting for big numbers: 3 significant digits with K, M, B, T, then aa, ab, ... (specs/03 section 4).
// Same output as the C# HeroCat.Sim.Format.BigNumber.Short. UI only; no game rule.
(function (root) {
  'use strict';
  function formatShort(v) {
    if (!isFinite(v)) return String(v);
    const neg = v < 0; v = Math.abs(v);
    if (v < 1000) {
      const s = v < 10 && v !== Math.floor(v) ? v.toFixed(2).replace(/\.?0+$/, '') : String(Math.round(v));
      return (neg ? '-' : '') + s;
    }
    const suffixes = ['', 'K', 'M', 'B', 'T'];
    let e = 0;
    while (v >= 999.5) { v /= 1000; e++; }
    let suf;
    if (e < suffixes.length) suf = suffixes[e];
    else {
      const k = e - suffixes.length; // 0 -> aa
      suf = String.fromCharCode(97 + Math.floor(k / 26)) + String.fromCharCode(97 + (k % 26));
    }
    const digits = v >= 100 ? 0 : v >= 10 ? 1 : 2;
    return (neg ? '-' : '') + v.toFixed(digits) + suf;
  }
  root.IdleFormat = { formatShort: formatShort };
})(typeof self !== 'undefined' ? self : this);
