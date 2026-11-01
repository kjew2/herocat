// Minimal static file server for the tests (wasm needs HTTP and application/wasm).
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml',
  '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.pdb': 'application/octet-stream', '.ico': 'image/x-icon' };
// -> Promise<{url, close}>
function start(rootDir, port) {
  const root = path.resolve(rootDir);
  const server = http.createServer((req, res) => {
    let p = decodeURIComponent(req.url.split('?')[0]);
    if (p.endsWith('/')) p += 'index.html';
    const f = path.join(root, path.normalize(p));
    if (!f.startsWith(root)) { res.writeHead(403); return res.end(); }
    fs.stat(f, (err, st) => {
      if (err || !st.isFile()) { res.writeHead(404); return res.end('not found'); }
      res.writeHead(200, { 'Content-Type': MIME[path.extname(f)] || 'application/octet-stream', 'Content-Length': st.size, 'Cache-Control': 'no-store' });
      if (req.method === 'HEAD') return res.end();
      fs.createReadStream(f).pipe(res);
    });
  });
  return new Promise((resolve) => server.listen(port || 0, '127.0.0.1', () => {
    resolve({ url: 'http://127.0.0.1:' + server.address().port + '/', close: () => new Promise((r) => server.close(r)) });
  }));
}
module.exports = { start };
if (require.main === module) start(process.argv[2] || path.join(__dirname, '..', '..'), +process.argv[3] || 8080).then((s) => console.log('serving ' + s.url));
