// 端到端延迟实测：不碰 UI，直接驱动 spectrum-sink.exe。
//
// 方法：生成「1s 静音 + 满幅咔哒(3 样本) + 2s 静音」的 WAV。PowerShell 用
// System.Media.SoundPlayer 播放（无窗口、不抢焦点），播前先打印自己的
// Unix 毫秒时刻。咔哒固定在 WAV 的第 1.000 秒，所以
//   预期起跳时刻 = 播放时刻 + 1000
// 宿主抓 WASAPI 环回，帧到达浏览器侧用 Date.now() 打墙钟戳。
//   实测延迟 = 第一个跳起的帧 - 预期起跳时刻
// 这条链路包含：渲染队列 → WASAPI 缓冲 → 回调 → 环形缓冲 → FFT 窗口
// → 帧循环 → 宿主滤波 → stdout → （本测试到此为止；JS 侧平滑另计）。
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const HOST = path.join(__dirname, 'spectrum-sink.exe');
const SR = 48000;
const CLICK_AT = 1.0;                          // 秒，WAV 内

function writeWav(file) {
  const n = SR * 3.0;
  const data = Buffer.alloc(n * 2);
  const at = Math.round(SR * CLICK_AT);
  data.writeInt16LE(32767, at * 2);
  data.writeInt16LE(-32768, (at + 1) * 2);
  data.writeInt16LE(16384, (at + 2) * 2);
  const hdr = Buffer.alloc(44);
  hdr.write('RIFF', 0); hdr.writeUInt32LE(36 + data.length, 4); hdr.write('WAVE', 8);
  hdr.write('fmt ', 12); hdr.writeUInt32LE(16, 16); hdr.writeUInt16LE(1, 20);
  hdr.writeUInt16LE(1, 22); hdr.writeUInt32LE(SR, 24); hdr.writeUInt32LE(SR * 2, 28);
  hdr.writeUInt16LE(2, 32); hdr.writeUInt16LE(16, 34);
  hdr.write('data', 36); hdr.writeUInt32LE(data.length, 40);
  fs.writeFileSync(file, Buffer.concat([hdr, data]));
}

const wav = path.join(process.env.TEMP, 'viz-click-test.wav');
writeWav(wav);

const host = spawn(HOST, [], { stdio: ['ignore', 'pipe', 'ignore'] });
let buf = '';
const frames = [];                             // {t: Date.now(), v:[64]}
host.stdout.on('data', (c) => {
  buf += c.toString();
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line.startsWith('{"event":"spectrum"')) continue;
    try {
      const j = JSON.parse(line);
      if (Array.isArray(j.v)) frames.push({ t: Date.now(), v: j.v });
    } catch (e) { /* 半截行 */ }
  }
});

let playStart = null;
const ps = spawn('powershell.exe', ['-NoProfile', '-Command',
  `[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()` +
  `; (New-Object Media.SoundPlayer '${wav}').PlaySync()`],
  { windowsHide: true });
ps.stdout.on('data', (c) => {
  const m = /(\d{13})/.exec(c.toString());
  if (m && playStart === null) playStart = Number(m[1]);
});

setTimeout(() => {
  host.kill();
  if (playStart === null) { console.log('FAIL 没拿到播放时刻'); process.exit(1); }
  const expect = playStart + CLICK_AT * 1000;

  const sums = frames.map(f => f.v.reduce((a, b) => a + b, 0));
  const maxSum = Math.max(...sums);
  if (maxSum < 100) { console.log('FAIL 全程无信号，宿主没抓到'); process.exit(1); }
  const thr = maxSum * 0.25;

  // 第一个「晚于播放开始、和值越过阈值」的帧即起跳帧
  let hit = null, hitIdx = -1;
  for (let i = 0; i < frames.length; i++) {
    if (frames[i].t < playStart) continue;
    if (sums[i] > thr) { hit = frames[i]; hitIdx = i; break; }
  }
  if (!hit) { console.log('FAIL 没找到跳变帧'); process.exit(1); }

  const latency = hit.t - expect;

  // 回落：跳变后多少帧 / 多少 ms 降到峰值 25% 以下
  let fall = null;
  for (let i = hitIdx; i < frames.length; i++) {
    if (sums[i] < maxSum * 0.25) { fall = frames[i].t - hit.t; break; }
  }

  // 帧率与帧间隔
  const gaps = [];
  for (let i = 1; i < frames.length; i++) gaps.push(frames[i].t - frames[i - 1].t);
  gaps.sort((a, b) => a - b);
  const medGap = gaps[Math.floor(gaps.length / 2)];

  console.log(`帧数        : ${frames.length}`);
  console.log(`峰值(和)    : ${maxSum}`);
  console.log(`预期起跳    : +${CLICK_AT * 1000} ms (播放时刻 ${playStart})`);
  console.log(`实测起跳    : ${hit.t}  (帧内和 ${sums[hitIdx]})`);
  console.log(`端到端延迟  : ${latency} ms   ← 采集+FFT+帧循环+宿主滤波+stdout`);
  console.log(`峰值回落到 25%: ${fall === null ? '>窗口' : fall + ' ms'}   ← 宿主侧滤波链的尾巴`);
  console.log(`帧间隔中位  : ${medGap} ms  (${(1000 / medGap).toFixed(1)} fps)`);
  process.exit(0);
}, 6500);