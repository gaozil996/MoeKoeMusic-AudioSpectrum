// 端到端延迟实测：不碰 UI，直接驱动 spectrum-sink.exe。
//
// 方法：clickplay.exe（WasapiOut 预灌内存缓冲，同 Measure.cs 已证 47ms 干净
// 路径）在第 1.000 / 2.000 秒各打一个 40ms 满幅噪声冲激，并把 Play() 时刻
// 打到 stdout。不用 SoundPlayer：它挂在 winmm 上，首个采样要多憋 ~100ms 且
// 逐次漂几十 ms，这一项既大又量不到插件的任何东西。
//
// 单轮判据（任一不过 → 这轮作废，不是报个假数字）：
//   1. 每个冲激取 [expect, expect+300ms] 窗内的峰值帧和 50% 首越帧；
//   2. 两个独立估计必须落在 ±40ms 内 —— 音乐节拍撞进两个已知窗口且给出
//      一致延迟的概率极低；
//   3. 窗内峰值须 > 1.6 倍窗内中位，否则视为没抓到（宿主没锁默认设备）；
//   4. 窗内中位 > 3000（满幅 16320）判废 —— 音乐还压在频谱上，节拍会把
//      首越/峰值随机挪 ±20ms；
//   5. 50% 首越 < 40ms 判废 —— 采集前半就 47ms，比它更早只可能是音乐
//      瞬态抢跑。
//
// 多轮取中位：用户可能正放着歌，个别轮会被节拍污染；中位数顶得住，
// 连续两轮都脏则整体 FAIL。
//
// spectrum-sink.exe 必须带 --default 启动：挑选器会在多个设备间来回翻，
// 冲激会掉进一个没人读的 ring。
const path = require('path');
const { spawn } = require('child_process');

const HOST = path.join(__dirname, 'spectrum-sink.exe');
const PLAYER = path.join(__dirname, 'clickplay.exe');
const CLICKS = [1.0, 2.0];                        // 秒，clickplay 内
const WIN = 300;                                  // 每个冲激的搜索窗，ms
const ROUNDS_TARGET = 4;                          // 至少凑齐的有效轮数
const ROUNDS_MAX = 7;                             // 最多尝试轮数

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

async function runOnce() {
  const host = spawn(HOST, ['--default'], { stdio: ['ignore', 'pipe', 'ignore'] });
  let buf = '';
  const frames = [];
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
  const pl = spawn(PLAYER, [], { stdio: ['ignore', 'pipe', 'pipe'] });
  pl.stdout.on('data', (c) => {
    const m = /(\d{13})/.exec(c.toString());
    if (m && playStart === null) playStart = Number(m[1]);
  });

  await sleep(6500);
  host.kill();
  try { pl.kill(); } catch (e) { }

  if (playStart === null) return { fail: '没拿到播放时刻' };

  const sums = frames.map(f => f.v.reduce((a, b) => a + b, 0));
  const est = CLICKS.map((sec) => {
    const expect = playStart + sec * 1000;
    const win = [];
    for (let i = 0; i < frames.length; i++) {
      const dt = frames[i].t - expect;
      if (dt >= 0 && dt <= WIN) win.push({ i, dt });
    }
    if (!win.length) return { fail: '窗口内没有帧' };
    const sortedSums = win.map(w => sums[w.i]).sort((a, b) => a - b);
    const med = sortedSums[Math.floor(sortedSums.length / 2)];
    let peak = win[0];
    for (const w of win) if (sums[w.i] > sums[peak.i]) peak = w;
    if (sums[peak.i] < med * 1.6)
      return { fail: `窗内无冲激（峰 ${sums[peak.i]} / 中位 ${med}）` };
    // 底噪门：窗内中位是全 64 band 的和（满幅 =16320），>3000 说明音乐还压在
    // 频谱上 —— 即使这轮能过一致性检查，节拍也把首越/峰值随机挪 ±20ms，
    // A/B 两组轮流采到不同音乐态就不公平了。宁可少一轮，只要干净数据。
    if (med > 3000) return { fail: `底噪过高（中位 ${med}），音乐污染` };
    // 50% 首越按「中位 + 冲激增量的一半」算，不按峰值百分比 —— 音乐垫底时
    // 峰值的 25% 可能低于地板，首越帧会退化成窗内第一帧（0ms 假数）。
    const thr = med + (sums[peak.i] - med) * 0.5;
    const first = win.find(w => sums[w.i] >= thr);
    const f = first ? first.dt : peak.dt;
    if (f < 40) return { fail: `首越 ${f}ms 早于采集下限 47ms，音乐瞬态抢跑` };
    return { peak: peak.dt, first: f };
  });

  if (est.some(e => e.fail)) return { fail: est.map((e, i) => `冲激${i + 1}: ${e.fail || 'ok'}`).join(' | ') };
  if (Math.abs(est[0].peak - est[1].peak) > 40)
    return { fail: `两冲激峰值不一致：${est[0].peak}ms vs ${est[1].peak}ms` };
  if (Math.abs(est[0].first - est[1].first) > 40)
    return { fail: `两冲激首越不一致：${est[0].first}ms vs ${est[1].first}ms` };
  const first = Math.round((est[0].first + est[1].first) / 2);
  const peak = Math.round((est[0].peak + est[1].peak) / 2);
  // 上升时长 = 首越→达峰。起播偏置（驱动启动、时钟差）对两个时刻同样生效，
  // 相减即抵消 —— A/B 对比攻击链时看这个量最干净。
  return { first, peak, rise: peak - first, gap: med2(frames) };
}

function med2(frames) {
  const g = [];
  for (let i = 1; i < frames.length; i++) g.push(frames[i].t - frames[i - 1].t);
  g.sort((a, b) => a - b);
  return g[Math.floor(g.length / 2)];
}

function median(a) {
  const s = [...a].sort((x, y) => x - y);
  return s[Math.floor(s.length / 2)];
}

(async () => {
  const good = [];
  for (let r = 1; r <= ROUNDS_MAX && good.length < ROUNDS_TARGET; r++) {
    const res = await runOnce();
    if (res.fail) {
      console.log(`第${r}轮: FAIL ${res.fail}`);
    } else {
      good.push(res);
      console.log(`第${r}轮: 50%首越 ${res.first}ms | 峰值帧 ${res.peak}ms | 上升 ${res.rise}ms`);
    }
    await sleep(500);
  }
  if (good.length < 2) {
    console.log(`FAIL 只凑齐 ${good.length} 轮有效数据（音乐一直没停 / 设备没锁住）`);
    process.exit(1);
  }
  const first = median(good.map(g => g.first));
  const peak = median(good.map(g => g.peak));
  console.log(`有效轮数      : ${good.length}/${ROUNDS_MAX}`);
  console.log(`响应(50%首越) : ≈${first} ms   ← 冲激开始被看见（感知起跳）`);
  console.log(`达峰          : ≈${peak} ms   ← 谱能量最盛（满幅响应）`);
  console.log(`上升时长      : ≈${median(good.map(g => g.rise))} ms   ← 首越→达峰（A/B 对比用）`);
  console.log(`帧间隔中位    : ${median(good.map(g => g.gap))} ms  (${(1000 / median(good.map(g => g.gap))).toFixed(1)} fps)`);
  process.exit(0);
})();
