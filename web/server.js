#!/usr/bin/env node
/*
 * Tuner — local server for the web IPTV player.
 *
 * Why a server at all? Browsers block a web page from talking to most IPTV
 * panels directly (no CORS headers, plain http links, redirects to token URLs).
 * This tiny relay fetches the Xtream Codes API and streams on the page's behalf.
 *
 * No dependencies. Needs Node.js 18 or newer.
 *   node server.js            -> http://localhost:8080
 *   PORT=9000 node server.js  -> another port
 *   HOST=0.0.0.0 node server.js  -> reachable from other devices on your network
 */
'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');
const { Readable } = require('stream');
const { spawn, spawnSync } = require('child_process');

const PORT = Number(process.env.PORT) || 8080;
const HOST = process.env.HOST || '127.0.0.1';
// Many panels only answer "player-like" clients.
const UA = process.env.IPTV_UA || 'VLC/3.0.20 LibVLC/3.0.20';
const PUBLIC = path.join(__dirname, 'public');
const MAX_PLAYLIST_BYTES = 8 * 1024 * 1024;
// Audio codec used when a movie's own audio can't play in browsers (AC-3, DTS…).
const AUDIO_OUT = (process.env.TUNER_AUDIO_CODEC || 'aac').toLowerCase() === 'opus' ? 'opus' : 'aac';

/* ---------- FFmpeg (optional: built-in subtitles, audio languages, MKV/AC-3 playback) ---------- */
function findBin(name) {
  const envPath = process.env[name.toUpperCase() + '_PATH'];
  const candidates = [envPath, name];
  // Copies installed into this folder by "npm install" (no admin rights needed)
  for (const mod of name === 'ffmpeg' ? ['ffmpeg-static', '@ffmpeg-installer/ffmpeg'] : ['@ffprobe-installer/ffprobe', 'ffprobe-static']) {
    try { const m = require(mod); candidates.push(typeof m === 'string' ? m : m && m.path); } catch {}
  }
  if (process.platform === 'win32') candidates.push(path.join(__dirname, 'ffmpeg', name + '.exe'));
  else candidates.push(path.join(__dirname, 'ffmpeg', name), '/opt/homebrew/bin/' + name, '/usr/local/bin/' + name);
  for (const c of candidates) {
    if (!c) continue;
    try { if (spawnSync(c, ['-version'], { stdio: 'ignore', timeout: 8000 }).status === 0) return c; } catch {}
  }
  return null;
}
const FFMPEG = findBin('ffmpeg');
const FFPROBE = FFMPEG && findBin('ffprobe');
const HAS_FF = !!(FFMPEG && FFPROBE);

const TEXT_SUBS = new Set(['subrip', 'srt', 'ass', 'ssa', 'webvtt', 'mov_text', 'text', 'microdvd', 'subviewer', 'subviewer1', 'sami', 'realtext', 'jacosub', 'mpl2', 'pjs', 'vplayer', 'stl']);
const BROWSER_AUDIO = new Set(['aac', 'mp3', 'opus']);
const COPY_VIDEO = new Set(['h264', 'hevc', 'vp9', 'av1']);

function parseTime(s) {
  const m = String(s).trim().match(/^(?:(\d+):)?(\d{1,2}):(\d{2})(?:[.,](\d{1,3}))?/);
  if (!m) return NaN;
  return (+m[1] || 0) * 3600 + +m[2] * 60 + +m[3] + (m[4] ? +m[4].padEnd(3, '0') / 1000 : 0);
}

const probeCache = new Map();
function probe(url) {
  const hit = probeCache.get(url);
  if (hit && Date.now() - hit.at < 15 * 60e3) return hit.p;
  const p = new Promise((resolve, reject) => {
    const args = ['-v', 'error', '-user_agent', UA, '-probesize', '10M', '-analyzeduration', '10M', '-print_format', 'json', '-show_format', '-show_streams', url];
    const ch = spawn(FFPROBE, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    let out = '', err = '';
    const timer = setTimeout(() => ch.kill('SIGKILL'), 30000);
    ch.stdout.on('data', (d) => (out += d));
    ch.stderr.on('data', (d) => (err += d));
    ch.on('error', reject);
    ch.on('close', (code) => {
      clearTimeout(timer);
      try { if (code !== 0) throw 0; resolve(describe(JSON.parse(out))); }
      catch { reject(new Error((err.trim().split('\n').pop() || 'Could not read this video').slice(0, 300))); }
    });
  });
  probeCache.set(url, { at: Date.now(), p });
  p.catch(() => probeCache.delete(url));
  return p;
}
function describe(j) {
  const streams = j.streams || [];
  const tag = (s, k) => (s.tags && (s.tags[k] || s.tags[k.toUpperCase()])) || '';
  const disp = (s, k) => !!(s.disposition && s.disposition[k]);
  const v = streams.find((s) => s.codec_type === 'video' && !disp(s, 'attached_pic'));
  const audio = streams.filter((s) => s.codec_type === 'audio').map((s) => ({
    index: s.index, codec: s.codec_name, lang: tag(s, 'language'), title: tag(s, 'title'), channels: s.channels || 0, default: disp(s, 'default'),
  }));
  const subs = streams.filter((s) => s.codec_type === 'subtitle').map((s) => ({
    index: s.index, codec: s.codec_name, lang: tag(s, 'language'), title: tag(s, 'title'),
    forced: disp(s, 'forced'), default: disp(s, 'default'), text: TEXT_SUBS.has(s.codec_name),
  }));
  const format = (j.format && j.format.format_name) || '';
  const duration = parseFloat(j.format && j.format.duration) || 0;
  const direct = /mp4|mov/.test(format) && v && v.codec_name === 'h264' && audio.length <= 1 &&
    audio.every((a) => BROWSER_AUDIO.has(a.codec)) && !subs.some((x) => x.text);
  return {
    duration, format, direct,
    video: v ? { index: v.index, codec: v.codec_name, width: v.width, height: v.height } : null,
    audio, subs,
  };
}

const slots = new Map();     // one running FFmpeg per player window → one connection to the provider
const sessions = new Map();  // subtitle cues and restart point for each playback
function killWait(child) {
  return new Promise((resolve) => {
    if (!child || child.exitCode !== null || child.signalCode) return resolve();
    const t = setTimeout(resolve, 2000);
    child.once('close', () => { clearTimeout(t); resolve(); });
    child.kill('SIGKILL');
  });
}
function pumpVtt(track, flush) {
  let i;
  while ((i = track.buf.indexOf('\n\n')) >= 0 || (flush && track.buf.trim())) {
    const block = i >= 0 ? track.buf.slice(0, i) : track.buf;
    track.buf = i >= 0 ? track.buf.slice(i + 2) : '';
    const lines = block.replace(/\r/g, '').split('\n');
    const k = lines.findIndex((l) => l.includes('-->'));
    if (k < 0) continue;
    const [a, z] = lines[k].split('-->');
    const s = parseTime(a), e = parseTime(z);
    const text = lines.slice(k + 1).join('\n').trim();
    if (text && e > s) track.cues.push([Math.round(s * 1000) / 1000, Math.round(e * 1000) / 1000, text]);
  }
}

async function remux(req, res, q) {
  if (!HAS_FF) return send(res, 501, 'FFmpeg is not installed');
  let url;
  try { url = new URL(q.get('url')); if (!/^https?:$/.test(url.protocol)) throw 0; } catch { return send(res, 400, 'Invalid url'); }
  let info;
  try { info = await probe(url.href); } catch (e) { return send(res, 502, e.message); }
  if (!info.video) return send(res, 415, 'No video in this file');

  const start = Math.max(0, parseFloat(q.get('start')) || 0);
  const encode = q.get('v') === 'encode' || !COPY_VIDEO.has(info.video.codec);
  const aParam = q.get('a');
  const audio = info.audio.find((a) => String(a.index) === aParam) || info.audio.find((a) => a.default) || info.audio[0];
  const texts = info.subs.filter((x) => x.text);
  const slot = (q.get('slot') || 'default').slice(0, 40);
  const sid = (q.get('sid') || 'x').slice(0, 40);

  await killWait(slots.get(slot));
  if (res.destroyed) return;

  const args = ['-hide_banner', '-loglevel', 'error', '-nostdin',
    '-user_agent', UA, '-reconnect', '1', '-reconnect_streamed', '1', '-reconnect_delay_max', '4',
    '-copyts', '-noaccurate_seek'];
  if (start > 0) args.push('-ss', start.toFixed(3));
  args.push('-i', url.href);
  // 1) picture + sound for the browser
  args.push('-map', `0:${info.video.index}`);
  if (audio) args.push('-map', `0:${audio.index}`);
  if (encode) args.push('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '21', '-pix_fmt', 'yuv420p', '-vf', 'scale=-2:min(1080\\,ih)', '-g', '48');
  else { args.push('-c:v', 'copy'); if (info.video.codec === 'hevc') args.push('-tag:v', 'hvc1'); }
  if (audio) {
    if (BROWSER_AUDIO.has(audio.codec) && !(AUDIO_OUT === 'opus' && audio.codec === 'aac')) args.push('-c:a', 'copy');
    else if (AUDIO_OUT === 'opus') args.push('-c:a', 'libopus', '-b:a', '160k', '-ac', '2');
    else args.push('-c:a', 'aac', '-b:a', '192k', '-ac', '2');
  }
  args.push('-max_muxing_queue_size', '4096', '-f', 'mp4', '-movflags', 'empty_moov+default_base_moof', '-frag_duration', '1000000', 'pipe:1');
  // 2) first video packet = exact restart point (keeps subtitles in sync after seeking)
  args.push('-map', `0:${info.video.index}`, '-c', 'copy', '-frames:v', '1', '-f', 'framecrc', 'pipe:3');
  // 3) every text subtitle track, as WebVTT, read in the same pass
  texts.forEach((t, i) => args.push('-map', `0:${t.index}`, '-c:s', 'webvtt', '-flush_packets', '1', '-f', 'webvtt', `pipe:${4 + i}`));

  const child = spawn(FFMPEG, args, { stdio: ['ignore', 'pipe', 'pipe', 'pipe', ...texts.map(() => 'pipe')] });
  slots.set(slot, child);
  const sess = { tracks: {}, keyframe: null, done: false, at: Date.now() };
  sessions.set(sid, sess);
  let stderr = '';
  child.stderr.on('data', (d) => { stderr = (stderr + d).slice(-4000); });
  let crc = '';
  child.stdio[3].on('data', (d) => {
    crc += d;
    const tb = crc.match(/#tb 0: (\d+)\/(\d+)/), pk = crc.match(/^0,\s*(-?\d+),/m);
    if (tb && pk && sess.keyframe == null) sess.keyframe = +pk[1] * +tb[1] / +tb[2];
  });
  texts.forEach((t, i) => {
    const tr = sess.tracks[t.index] = { buf: '', cues: [] };
    const st = child.stdio[4 + i];
    st.setEncoding('utf8');
    st.on('data', (d) => { tr.buf += d; pumpVtt(tr, false); });
  });

  let started = false;
  child.stdout.on('data', (chunk) => {
    if (!started) {
      started = true;
      res.writeHead(200, { 'content-type': 'video/mp4', 'cache-control': 'no-store', 'access-control-allow-origin': '*' });
    }
    if (!res.write(chunk)) { child.stdout.pause(); res.once('drain', () => child.stdout.resume()); }
  });
  child.on('error', () => {});
  child.on('close', () => {
    sess.done = true;
    for (const tr of Object.values(sess.tracks)) pumpVtt(tr, true);
    if (slots.get(slot) === child) slots.delete(slot);
    if (!started) send(res, 502, 'FFmpeg could not play this video: ' + (stderr.trim().split('\n').pop() || 'unknown error'));
    else res.end();
    setTimeout(() => { if (sessions.get(sid) === sess) sessions.delete(sid); }, 3 * 3600e3);
  });
  res.on('close', () => { if (child.exitCode === null && !child.signalCode) child.kill('SIGKILL'); });
}

function remuxState(res, q) {
  const sess = sessions.get(q.get('sid') || '');
  if (!sess) return send(res, 404, JSON.stringify({ error: 'unknown session' }), 'application/json');
  const from = {};
  for (const pair of (q.get('f') || '').split(',')) { const [k, v] = pair.split(':'); if (k) from[k] = +v || 0; }
  const tracks = {};
  for (const [k, tr] of Object.entries(sess.tracks)) tracks[k] = tr.cues.slice(from[k] || 0);
  send(res, 200, JSON.stringify({ keyframe: sess.keyframe, done: sess.done, tracks }), 'application/json');
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.json': 'application/json',
};

function send(res, status, body, type = 'text/plain; charset=utf-8') {
  if (res.headersSent) return res.end();
  res.writeHead(status, {
    'content-type': type,
    'access-control-allow-origin': '*',
    'cache-control': 'no-store',
  });
  res.end(body);
}

/* ---------- static files ---------- */
function serveStatic(req, res, pathname) {
  let rel = decodeURIComponent(pathname);
  if (rel === '/' || rel === '') rel = '/index.html';
  const file = path.normalize(path.join(PUBLIC, rel));
  if (!file.startsWith(PUBLIC)) return send(res, 403, 'Forbidden');
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) return send(res, 404, 'Not found');
    res.writeHead(200, {
      'content-type': MIME[path.extname(file)] || 'application/octet-stream',
      'content-length': st.size,
      'cache-control': rel.startsWith('/vendor/') ? 'max-age=86400' : 'no-cache',
    });
    fs.createReadStream(file).pipe(res);
  });
}

/* ---------- the relay ---------- */
const wrap = (uri, base) => {
  try { return '/proxy?url=' + encodeURIComponent(new URL(uri, base).href); }
  catch { return uri; }
};

// Point every segment / sub-playlist / key inside an HLS playlist back through the relay.
function rewritePlaylist(text, base) {
  return text.split(/\r?\n/).map((line) => {
    const t = line.trim();
    if (!t) return line;
    if (t.startsWith('#')) return line.replace(/URI="([^"]+)"/g, (_, u) => `URI="${wrap(u, base)}"`);
    return wrap(t, base);
  }).join('\n');
}

async function readCapped(body, cap) {
  const reader = body.getReader();
  const chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > cap) { reader.cancel().catch(() => {}); throw new Error('Playlist too large'); }
    chunks.push(value);
  }
  return Buffer.concat(chunks.map((c) => Buffer.from(c)));
}

async function relay(req, res, target) {
  let url;
  try { url = new URL(target); } catch { return send(res, 400, 'Invalid url'); }
  if (!/^https?:$/.test(url.protocol)) return send(res, 400, 'Only http and https are supported');

  const ac = new AbortController();
  res.on('close', () => { if (!res.writableEnded) ac.abort(); });

  const headers = { 'user-agent': UA, accept: '*/*' };
  if (req.headers.range) headers.range = req.headers.range;

  let up;
  try {
    up = await fetch(url, { headers, redirect: 'follow', signal: ac.signal });
  } catch (e) {
    if (ac.signal.aborted) return;
    const reason = e.cause && e.cause.code ? e.cause.code : e.message;
    return send(res, 502, `Could not reach the server (${reason})`);
  }

  const finalUrl = up.url || url.href;
  const ct = (up.headers.get('content-type') || '').toLowerCase();
  const looksM3u8 = /mpegurl/.test(ct) ||
    ((/\.m3u8?(\?|$)/i.test(finalUrl) || /\.m3u8?(\?|$)/i.test(url.pathname)) && !/^video\/|octet-stream/.test(ct));

  const out = {
    'access-control-allow-origin': '*',
    'access-control-expose-headers': 'content-length, content-range, accept-ranges',
    'cache-control': 'no-store',
  };

  if (looksM3u8 && up.ok) {
    let buf;
    try { buf = await readCapped(up.body, MAX_PLAYLIST_BYTES); }
    catch (e) { return send(res, 502, e.message); }
    const text = buf.toString('utf8');
    if (text.trimStart().startsWith('#EXTM3U') && /#EXT-X-|#EXTINF/.test(text) && !/tvg-|group-title/.test(text)) {
      const body = rewritePlaylist(text, finalUrl);
      res.writeHead(up.status, { ...out, 'content-type': 'application/vnd.apple.mpegurl' });
      return res.end(body);
    }
    res.writeHead(up.status, { ...out, 'content-type': ct || 'text/plain' });
    return res.end(buf);
  }

  for (const h of ['content-type', 'content-length', 'content-range', 'accept-ranges', 'last-modified']) {
    const v = up.headers.get(h);
    if (v) out[h] = v;
  }
  // fetch() transparently decompresses, so the upstream length may be wrong.
  if (up.headers.get('content-encoding')) delete out['content-length'];

  res.writeHead(up.status, out);
  if (!up.body || req.method === 'HEAD') return res.end();
  const stream = Readable.fromWeb(up.body);
  stream.on('error', () => res.destroy());
  stream.pipe(res);
}

/* ---------- built-in demo panel (public test streams) ---------- */
const GTV = 'https://commondatastorage.googleapis.com/gtv-videos-bucket/sample';
const DEMO = {
  live: [
    { id: 1, name: 'Mux Test Channel', cat: '1', url: 'https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8' },
    { id: 2, name: 'Apple BipBop', cat: '1', url: 'https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8' },
    { id: 3, name: 'Akamai Live Test', cat: '2', url: 'https://cph-p2p-msl.akamaized.net/hls/live/2000341/test/master.m3u8' },
    { id: 4, name: 'Tears of Steel', cat: '2', url: 'https://demo.unified-streaming.com/k8s/features/stable/video/tears-of-steel/tears-of-steel.ism/.m3u8' },
  ],
  liveCats: [{ category_id: '1', category_name: 'Demo · Test Signals' }, { category_id: '2', category_name: 'Demo · Showcase' }],
  vod: [
    { id: 101, name: 'Big Buck Bunny', file: 'BigBuckBunny', year: '2008', rating: '6.4', plot: 'A giant rabbit takes revenge on three bullying rodents. Blender Foundation open movie.' },
    { id: 102, name: 'Elephants Dream', file: 'ElephantsDream', year: '2006', rating: '5.9', plot: 'Two men explore a strange, ever-changing machine world. The first Blender open movie.' },
    { id: 103, name: 'Sintel', file: 'Sintel', year: '2010', rating: '7.4', plot: 'A lonely girl searches for the baby dragon she raised. Blender Foundation open movie.' },
    { id: 104, name: 'Tears of Steel', file: 'TearsOfSteel', year: '2012', rating: '6.1', plot: 'Scientists in Amsterdam try to save the world from robots. Blender Foundation open movie.' },
  ],
  vodCats: [{ category_id: '10', category_name: 'Demo · Open Movies' }],
  seriesCats: [{ category_id: '20', category_name: 'Demo · Shorts' }],
  episodes: [
    { id: 201, title: 'For Bigger Blazes', file: 'ForBiggerBlazes' },
    { id: 202, title: 'For Bigger Escapes', file: 'ForBiggerEscapes' },
    { id: 203, title: 'For Bigger Fun', file: 'ForBiggerFun' },
    { id: 204, title: 'For Bigger Joyrides', file: 'ForBiggerJoyrides' },
  ],
};
const b64 = (s) => Buffer.from(s, 'utf8').toString('base64');

function demo(req, res, pathname, q) {
  const json = (o) => send(res, 200, JSON.stringify(o), 'application/json');
  if (pathname === '/demo/player_api.php') {
    const now = Math.floor(Date.now() / 1000);
    switch (q.get('action')) {
      case null:
        return json({
          user_info: { username: 'demo', auth: 1, status: 'Active', exp_date: String(now + 86400 * 365), max_connections: '1', active_cons: '0', allowed_output_formats: ['m3u8', 'ts'] },
          server_info: { url: 'localhost', timezone: 'UTC', time_now: new Date().toISOString() },
        });
      case 'get_live_categories': return json(DEMO.liveCats);
      case 'get_live_streams': return json(DEMO.live.map((c, i) => ({ num: i + 1, name: c.name, stream_type: 'live', stream_id: c.id, stream_icon: '', epg_channel_id: 'demo' + c.id, category_id: c.cat })));
      case 'get_vod_categories': return json(DEMO.vodCats);
      case 'get_vod_streams': return json(DEMO.vod.map((m, i) => ({ num: i + 1, name: m.name, stream_type: 'movie', stream_id: m.id, stream_icon: `${GTV}/images/${m.file}.jpg`, rating: m.rating, category_id: '10', container_extension: 'mp4', added: String(now - i * 86400) })));
      case 'get_vod_info': {
        const m = DEMO.vod.find((x) => String(x.id) === q.get('vod_id')) || DEMO.vod[0];
        return json({ info: { name: m.name, movie_image: `${GTV}/images/${m.file}.jpg`, backdrop_path: [`${GTV}/images/${m.file}.jpg`], plot: m.plot, genre: 'Animation · Open movie', releasedate: m.year, rating: m.rating, duration: '', director: 'Blender Foundation' }, movie_data: { stream_id: m.id, name: m.name, container_extension: 'mp4' } });
      }
      case 'get_series_categories': return json(DEMO.seriesCats);
      case 'get_series': return json([{ num: 1, name: 'Chromecast Shorts', series_id: 301, cover: `${GTV}/images/ForBiggerBlazes.jpg`, plot: 'Four short sample clips, grouped as a series so you can try the season and episode view.', rating: '7', releaseDate: '2015', category_id: '20', genre: 'Sample' }]);
      case 'get_series_info':
        return json({
          info: { name: 'Chromecast Shorts', cover: `${GTV}/images/ForBiggerBlazes.jpg`, plot: 'Four short sample clips, grouped as a series so you can try the season and episode view.', genre: 'Sample', releaseDate: '2015', rating: '7', backdrop_path: [`${GTV}/images/ForBiggerEscapes.jpg`] },
          episodes: {
            1: DEMO.episodes.slice(0, 2).map((e, i) => ({ id: String(e.id), episode_num: i + 1, title: e.title, container_extension: 'mp4', season: 1, info: { movie_image: `${GTV}/images/${e.file}.jpg`, plot: 'Sample clip.', duration: '00:00:15' } })),
            2: DEMO.episodes.slice(2).map((e, i) => ({ id: String(e.id), episode_num: i + 1, title: e.title, container_extension: 'mp4', season: 2, info: { movie_image: `${GTV}/images/${e.file}.jpg`, plot: 'Sample clip.', duration: '00:00:15' } })),
          },
        });
      case 'get_short_epg': {
        const slot = 30 * 60;
        const start = now - (now % slot);
        const shows = ['Signal Check', 'Colour Bars & Tone', 'Evening Showcase', 'Late Test Card'];
        return json({ epg_listings: shows.map((t, i) => ({ title: b64(t), description: b64('Demo programme guide entry.'), start_timestamp: String(start + i * slot), stop_timestamp: String(start + (i + 1) * slot) })) });
      }
      default: return json([]);
    }
  }
  const m = pathname.match(/^\/demo\/(live|movie|series)\/[^/]+\/[^/]+\/(\d+)\.(\w+)$/);
  if (m) {
    const id = Number(m[2]);
    let to;
    if (m[1] === 'live') to = (DEMO.live.find((c) => c.id === id) || {}).url;
    if (m[1] === 'movie') { const v = DEMO.vod.find((x) => x.id === id); to = v && `${GTV}/${v.file}.mp4`; }
    if (m[1] === 'series') { const e = DEMO.episodes.find((x) => x.id === id); to = e && `${GTV}/${e.file}.mp4`; }
    if (m[1] === 'live' && m[3] !== 'm3u8') return send(res, 404, 'Demo channels are HLS only');
    if (to) { res.writeHead(302, { location: to }); return res.end(); }
  }
  return send(res, 404, 'Not found');
}

/* ---------- router ---------- */
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://local');
  if (req.method === 'OPTIONS') {
    res.writeHead(204, { 'access-control-allow-origin': '*', 'access-control-allow-headers': 'range', 'access-control-allow-methods': 'GET, HEAD' });
    return res.end();
  }
  if (u.pathname === '/proxy') {
    const target = u.searchParams.get('url');
    if (!target) return send(res, 400, 'Missing url');
    return relay(req, res, target).catch((e) => send(res, 500, e.message));
  }
  if (u.pathname === '/media') {
    if (!HAS_FF) return send(res, 501, JSON.stringify({ error: 'ffmpeg' }), 'application/json');
    let t; try { t = new URL(u.searchParams.get('url')); if (!/^https?:$/.test(t.protocol)) throw 0; } catch { return send(res, 400, 'Invalid url'); }
    return probe(t.href).then((info) => send(res, 200, JSON.stringify(info), 'application/json'))
      .catch((e) => send(res, 502, JSON.stringify({ error: e.message }), 'application/json'));
  }
  if (u.pathname === '/remux') return remux(req, res, u.searchParams).catch((e) => send(res, 500, e.message));
  if (u.pathname === '/remux/state') return remuxState(res, u.searchParams);
  if (u.pathname === '/features') return send(res, 200, JSON.stringify({ ffmpeg: HAS_FF }), 'application/json');
  if (u.pathname.startsWith('/demo/')) return demo(req, res, u.pathname, u.searchParams);
  if (req.method !== 'GET' && req.method !== 'HEAD') return send(res, 405, 'Method not allowed');
  return serveStatic(req, res, u.pathname);
});

server.on('error', (e) => {
  if (e.code === 'EADDRINUSE') console.error(`\n  Port ${PORT} is busy. Try:  PORT=9000 node server.js\n`);
  else console.error(e);
  process.exit(1);
});

server.listen(PORT, HOST, () => {
  const shown = HOST === '0.0.0.0' ? 'localhost' : HOST === '127.0.0.1' ? 'localhost' : HOST;
  console.log(`\n  Tuner is running →  http://${shown}:${PORT}`);
  console.log(HAS_FF
    ? `  FFmpeg found: built-in subtitles, audio languages and MKV playback are on.`
    : `  FFmpeg not found: movies still play, but built-in subtitles and audio languages are off.\n  See README.md → "Built-in subtitles" to install it.`);
  console.log(`  Press Ctrl+C to stop.\n`);
});
