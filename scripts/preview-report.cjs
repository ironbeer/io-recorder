// Optional local-only report preview: node scripts/preview-report.cjs <session-folder> [port]
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const report = path.resolve(process.argv[2] || '.', 'report.html');
const port = Number(process.argv[3] || 8876);
if (!fs.existsSync(report)) throw new Error('report.html was not found');
http.createServer((req, res) => {
  if (req.url !== '/' && req.url !== '/report.html') { res.writeHead(404).end(); return; }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  fs.createReadStream(report).pipe(res);
}).listen(port, '127.0.0.1', () => console.log(`Report: http://127.0.0.1:${port}/`));
