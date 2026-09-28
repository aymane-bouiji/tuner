// Fake Xtream Codes panel for testing. Port 9100. user "alice" / pass "s3cret"
const http = require('http'), fs = require('fs'), path = require('path');
const D = require('path').join(__dirname, 'fixtures');
const json = (res, o) => { res.writeHead(200, { 'content-type': 'text/html' }); res.end(JSON.stringify(o)); }; // panels often send wrong content-type
const cats = Array.from({ length: 30 }, (_, i) => ({ category_id: String(i + 1), category_name: ['News', 'Sports', 'Movies', 'Kids', 'Music', 'Documentary'][i % 6] + (i >= 6 ? ' ' + (Math.floor(i / 6) + 1) : '') }));
const live = [
  { num: 1, name: 'Test HLS One', stream_id: 1, stream_icon: '', category_id: '1' },
  { num: 2, name: 'TS Only Channel', stream_id: 2, stream_icon: 'http://127.0.0.1:9100/missing.png', category_id: '1' },
  { num: 4, name: 'Subtitled Channel', stream_id: 4, stream_icon: '', category_id: '2' },
  { num: 3, name: 'Dead Channel', stream_id: 3, stream_icon: '', category_id: '2' },
  ...Array.from({ length: 3000 }, (_, i) => ({ num: i + 4, name: `Filler ${['Noticias', 'Sport', 'Cinéma', 'Kids'][i % 4]} ${i}`, stream_id: 1000 + i, stream_icon: '', category_id: String((i % 30) + 1) })),
];
http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x'); const q = u.searchParams;
  if (u.pathname === '/player_api.php') {
    if (q.get('username') !== 'alice' || q.get('password') !== 's3cret') return json(res, { user_info: { auth: 0 } });
    const a = q.get('action');
    if (!a) return json(res, { user_info: { username: 'alice', auth: 1, status: 'Active', exp_date: String(Math.floor(Date.now() / 1000) + 86400 * 40), max_connections: '2', active_cons: '1', allowed_output_formats: ['m3u8', 'ts', 'rtmp'] }, server_info: {} });
    if (a === 'get_live_categories') return json(res, cats);
    if (a === 'get_live_streams') return json(res, live);
    if (a === 'get_vod_categories') return json(res, cats.slice(0, 3));
    if (a === 'get_vod_streams') return json(res, Array.from({ length: 400 }, (_, i) => ({ num: i + 1, name: `Movie Title Number ${i + 1}`, stream_id: 5000 + i, stream_icon: '', rating: (5 + (i % 5)).toString(), category_id: String((i % 3) + 1), container_extension: i === 1 ? 'mkv' : 'mp4', year: String(1990 + (i % 30)) })));
    if (a === 'get_vod_info') return json(res, { info: { name: 'Movie Title Number ' + (q.get('vod_id') - 4999), plot: 'A test pattern goes on a long journey across a sea of colour bars.', genre: 'Drama, Test', duration: '00:01:30', releasedate: '2021-04-01', rating: '7.2', cast: 'Bar Smpte, Tone Sine', director: 'Ffmpeg' }, movie_data: { stream_id: q.get('vod_id'), container_extension: q.get('vod_id') === '5001' ? 'mkv' : 'mp4' } });
    if (a === 'get_series_categories') return json(res, cats.slice(0, 2));
    if (a === 'get_series') return json(res, [{ num: 1, name: 'The Test Show', series_id: 77, cover: '', rating: '8.1', releaseDate: '2019-01-01', category_id: '1' }]);
    if (a === 'get_series_info') return json(res, { info: { name: 'The Test Show', plot: 'Weekly adventures of a sine wave.', genre: 'Comedy' }, episodes: { 1: [{ id: '9001', episode_num: 1, title: 'Pilot', container_extension: 'mp4', info: { duration: '00:01:30', plot: 'It begins.' } }, { id: '9002', episode_num: 2, title: 'Second', container_extension: 'mkv', info: {} }], 2: [{ id: '9003', episode_num: 1, title: 'Return', container_extension: 'mp4', info: {} }] } });
    if (a === 'get_short_epg') { const n = Math.floor(Date.now() / 1000); const b = (s) => Buffer.from(s).toString('base64'); return json(res, { epg_listings: [{ title: b('Evening News — édition spéciale'), description: b('Headlines and weather.'), start_timestamp: String(n - 900), stop_timestamp: String(n + 900) }, { title: b('Late Film'), description: b(''), start_timestamp: String(n + 900), stop_timestamp: String(n + 4500) }] }); }
    return json(res, []);
  }
  // live 1: m3u8 redirects to token host path with relative segments
  if (u.pathname === '/live/alice/s3cret/1.m3u8') { res.writeHead(302, { location: 'http://127.0.0.1:9100/hlsr/token123/index.m3u8' }); return res.end(); }
  if (u.pathname.startsWith('/hlsr/token123/')) {
    const f = path.join(D, 'hls', path.basename(u.pathname));
    if (!fs.existsSync(f)) { res.writeHead(404); return res.end(); }
    res.writeHead(200, { 'content-type': f.endsWith('.m3u8') ? 'application/vnd.apple.mpegurl' : f.endsWith('.vtt') ? 'text/vtt' : 'video/mp2t' });
    return fs.createReadStream(f).pipe(res);
  }
  if (u.pathname === '/live/alice/s3cret/4.m3u8') { res.writeHead(302, { location: 'http://127.0.0.1:9100/hlsr/token123/master.m3u8' }); return res.end(); }
  // live 2: m3u8 not available, ts works
  if (u.pathname === '/live/alice/s3cret/2.m3u8') { res.writeHead(404); return res.end('no'); }
  if (u.pathname === '/live/alice/s3cret/2.ts') { res.writeHead(200, { 'content-type': 'video/mp2t' }); return fs.createReadStream(path.join(D, 'live.ts')).pipe(res); }
  // movies/series: mp4 or mkv with range support; tracks concurrent connections
  const mm = u.pathname.match(/^\/(movie|series)\/alice\/s3cret\/\d+\.(mp4|mkv)$/);
  if (mm) {
    global.open = (global.open || 0) + 1; global.maxOpen = Math.max(global.maxOpen || 0, global.open); global.total = (global.total || 0) + 1;
    res.on('close', () => { global.open--; });
    const f = path.join(D, 'movie.' + mm[2]), size = fs.statSync(f).size, ct = mm[2] === 'mkv' ? 'video/x-matroska' : 'video/mp4';
    const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || '');
    if (m) { const s = +m[1], e = m[2] ? +m[2] : size - 1; res.writeHead(206, { 'content-type': ct, 'content-length': e - s + 1, 'content-range': `bytes ${s}-${e}/${size}`, 'accept-ranges': 'bytes' }); return fs.createReadStream(f, { start: s, end: e }).pipe(res); }
    res.writeHead(200, { 'content-type': ct, 'content-length': size, 'accept-ranges': 'bytes' }); return fs.createReadStream(f).pipe(res);
  }
  if (u.pathname === '/stats') { res.writeHead(200); return res.end(JSON.stringify({ open: global.open || 0, maxOpen: global.maxOpen || 0, total: global.total || 0 })); }
  if (u.pathname === '/stats/reset') { global.maxOpen = global.open || 0; global.total = 0; res.writeHead(200); return res.end('ok'); }
  res.writeHead(404); res.end('nope');
}).listen(9100, '127.0.0.1', () => console.log('mock panel on 9100'));
