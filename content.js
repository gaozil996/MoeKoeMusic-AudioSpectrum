(function () {
  'use strict';

  /* ============================================================
     全屏歌词页的音乐频谱
     ------------------------------------------------------------
     数据：真实音频。native host 用 WASAPI 环回抓系统正在播放的声音，
     做 FFT 后给出 64 个对数频段（0-255）以及每段的峰值。完全在系统层
     捕获，不碰播放链路，音频质量和音量都不受影响。

     八种样式参考 hvianna/audioMotion-analyzer 的官方模式集：
     经典柱 / LED 点阵 / 镜像 / 波形 / 环形 / 折线 / 面积 / 反射。

     手感的核心不是插值系数，而是三件事：
       · 帧率无关的指数平滑（1 - e^(-dt/tau)），掉帧时不会变速
       · 低频给更长的 tau，视觉上沉稳；高频给短的，跟得上鼓点
       · 峰值两段式：先保持 HOLD_MS，再按 fade 淡出，而不是匀速掉
     ============================================================ */

  var KEY = 'viz_cfg';
  var CFG = {
    on: true,
    style: 'mirror',
    height: 150,
    opacity: 0.72,
    color: '#ff4d8d',
    glow: 0.55,
    smoothing: 1.0,     // 0..2，越大越黏
    peakHold: 520,      // ms，峰值保持
    peakFade: 780       // ms，峰值淡出
  };

  function log() { console.log.apply(console, ['[Viz]'].concat([].slice.call(arguments))); }

  /* ---------- 数据 ---------- */

  var BANDS = 64;
  var target = new Array(BANDS).fill(0);
  var level = new Array(BANDS).fill(0);
  var peak = new Array(BANDS).fill(0);
  var peakAt = new Float64Array(BANDS);
  var lastFrameAt = 0;
  var alive = false;

  chrome.runtime.onMessage.addListener(function (req) {
    if (!req) return;
    if (req.type === 'viz-diag') { log(req.msg); return; }
    if (req.type === 'viz-spectrum' && req.v && req.v.length) {
      var n = Math.min(BANDS, req.v.length);
      for (var i = 0; i < n; i++) target[i] = req.v[i];
      lastFrameAt = performance.now();
      alive = true;
    }
  });

  /* ---------- 颜色 ---------- */

  function parseHex(hex) {
    var h = String(hex || '').replace('#', '');
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    if (h.length !== 6 || /[^0-9a-f]/i.test(h)) return [255, 77, 141];
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }
  function rgba(hex, a) {
    var c = parseHex(hex);
    return 'rgba(' + c[0] + ',' + c[1] + ',' + c[2] + ',' + a + ')';
  }
  function mix(hex, to, t) {
    var a = parseHex(hex), b = parseHex(to);
    return 'rgb(' + Math.round(a[0] + (b[0] - a[0]) * t) + ',' +
      Math.round(a[1] + (b[1] - a[1]) * t) + ',' +
      Math.round(a[2] + (b[2] - a[2]) * t) + ')';
  }

  /* ---------- 动画：帧率无关的柔性跟随 ---------- */

  var lastTick = 0;

  function animate(dt) {
    var stale = performance.now() - lastFrameAt > 400;
    if (stale) alive = false;

    var now = performance.now();
    var k = CFG.smoothing;
    for (var i = 0; i < BANDS; i++) {
      var t = alive ? target[i] / 255 : 0;

      // tau 随频率递减：低频沉、高频脆。
      var f = i / (BANDS - 1);
      var tau = (0.135 - 0.085 * f) / Math.max(0.35, k);
      // 指数逼近；dt 参与计算，所以 30fps 和 144fps 下的观感一致。
      var a = 1 - Math.exp(-dt / tau);
      var prev = level[i];
      level[i] += (t - level[i]) * a;

      // 峰值：保持 -> 淡出。两段式比匀速回落自然得多。
      if (level[i] > peak[i]) {
        peak[i] = level[i];
        peakAt[i] = now;
      } else if (now - peakAt[i] > CFG.peakHold) {
        var fade = CFG.peakFade / 1000;
        peak[i] -= dt / fade;
        if (peak[i] < level[i]) peak[i] = level[i];
        if (peak[i] < 0.002) peak[i] = 0;
      }
      if (prev < 0.002 && level[i] < 0.002) { level[i] = 0; peak[i] = 0; }
    }
  }

  /* ---------- 采样：把 64 段重采样成任意点数 ---------- */

  // 线性插值重采样。频段少的时候用 nearest 会看出明显的台阶，
  // 波形和折线这两种样式尤其明显。
  function sample(pts, out) {
    for (var i = 0; i < pts; i++) {
      var x = pts === 1 ? 0 : (i / (pts - 1)) * (BANDS - 1);
      var i0 = Math.floor(x);
      var i1 = Math.min(BANDS - 1, i0 + 1);
      var f = x - i0;
      out[i] = level[i0] + (level[i1] - level[i0]) * f;
    }
    return out;
  }

  /* ---------- 绘制基元 ---------- */

  function roundRect(c, x, y, w, h, r) {
    if (h <= 0 || w <= 0) return;
    r = Math.max(0, Math.min(r, w / 2, h / 2));
    c.beginPath();
    c.moveTo(x + r, y);
    c.lineTo(x + w - r, y);
    c.quadraticCurveTo(x + w, y, x + w, y + r);
    c.lineTo(x + w, y + h);
    c.lineTo(x, y + h);
    c.lineTo(x, y + r);
    c.quadraticCurveTo(x, y, x + r, y);
    c.closePath();
    c.fill();
  }

  var buf = new Array(512);

  /* ---------- 八种样式 ---------- */

  var STYLES = {

    // 1. 经典柱：单向，从底部长上来
    classic: function (c, W, H) {
      var n = 64;
      var slot = W / n;
      var bw = Math.max(1, slot * 0.62);
      var baseY = H - 1;
      var maxH = H * 0.96;
      var g = c.createLinearGradient(0, H, 0, 0);
      g.addColorStop(0, rgba(CFG.color, 1));
      g.addColorStop(0.6, rgba(CFG.color, 0.82));
      g.addColorStop(1, rgba(CFG.color, 0.42));
      c.fillStyle = g;
      for (var i = 0; i < n; i++) {
        var h = level[i] * maxH;
        if (h < 0.7) continue;
        roundRect(c, i * slot + (slot - bw) / 2, baseY - h, bw, h, Math.min(bw / 2, 2 * dpr));
      }
    },

    // 2. LED 点阵：柱体由小方块堆叠，像点阵屏
    led: function (c, W, H) {
      var n = 48;
      var slot = W / n;
      var dot = Math.max(1.5, Math.min(slot * 0.52, 7 * dpr));
      var gap = Math.max(1, dot * 0.34);
      var cols = n;
      var rows = Math.max(2, Math.floor((H * 0.96) / (dot + gap)));
      var baseY = H - 1;
      for (var i = 0; i < cols; i++) {
        var lit = Math.round(level[i] * rows);
        for (var r = 0; r < lit; r++) {
          var y = baseY - r * (dot + gap) - dot;
          var t = r / rows;
          c.fillStyle = mix(CFG.color, '#ffffff', t * 0.35);
          c.globalAlpha = 0.45 + 0.55 * (1 - t * 0.7);
          c.fillRect(i * slot + (slot - dot) / 2, y, dot, dot);
        }
      }
      c.globalAlpha = 1;
    },

    // 3. 镜像：低频在正中心，向两侧对称展开
    mirror: function (c, W, H) {
      var half = BANDS;
      var slots = half * 2 - 1;
      var slot = W / slots;
      var bw = Math.max(1, slot * 0.44);
      var baseY = H - 1;
      var maxH = H * 0.95;
      var g = c.createLinearGradient(0, H, 0, 0);
      g.addColorStop(0, rgba(CFG.color, 1));
      g.addColorStop(0.55, rgba(CFG.color, 0.8));
      g.addColorStop(1, rgba(CFG.color, 0.34));
      c.fillStyle = g;
      for (var s = 0; s < slots; s++) {
        var b = Math.abs(half - 1 - s);
        var h = level[b] * maxH;
        if (h < 0.7) continue;
        roundRect(c, s * slot + (slot - bw) / 2, baseY - h, bw, h, Math.min(bw / 2, 2 * dpr));
      }
    },

    // 4. 波形：以画布中线为中心上下对称振动的真实波形
    //
    // 之前这一版是「顶边起伏 + 往下实心填到画布底部」，底边永远是直线、
    // 那一坨实心块又厚又不动，看上去就是一整块被抬起来的色板，而不是波形。
    // 真正的波形是围绕中线对称的：安静时收成一条细线，响时才向两边张开，
    // 上下起伏一样多，重心始终不动。
    wave: function (c, W, H) {
      var n = 220;
      sample(n, buf);

      var mid = H * 0.5;
      var amp = H * 0.46;

      // 上半：从中线往上
      c.beginPath();
      for (var i = 0; i < n; i++) {
        var x = (i / (n - 1)) * W;
        var y = mid - buf[i] * amp;
        if (i === 0) c.moveTo(x, y); else c.lineTo(x, y);
      }
      // 下半：从中线往下，原路折回，闭成一个对称的波形块
      for (var j = n - 1; j >= 0; j--) {
        c.lineTo((j / (n - 1)) * W, mid + buf[j] * amp);
      }
      c.closePath();

      var g = c.createLinearGradient(0, 0, 0, H);
      g.addColorStop(0, rgba(CFG.color, 0.30));
      g.addColorStop(0.5, rgba(CFG.color, 0.95));
      g.addColorStop(1, rgba(CFG.color, 0.30));
      c.fillStyle = g;
      c.fill();

      // 中线：一根极淡的参考线，静音时波形正好贴在上面
      c.strokeStyle = rgba(CFG.color, 0.22);
      c.lineWidth = 1 * dpr;
      c.beginPath();
      c.moveTo(0, mid); c.lineTo(W, mid);
      c.stroke();

      // 描上下两条包络，安静时两条重合，看得出是一条线而不是一块板
      c.strokeStyle = rgba(CFG.color, 0.95);
      c.lineWidth = 1.6 * dpr;
      c.lineJoin = 'round';
      c.beginPath();
      for (var a = 0; a < n; a++) {
        var xa = (a / (n - 1)) * W;
        var ya = mid - buf[a] * amp;
        if (a === 0) c.moveTo(xa, ya); else c.lineTo(xa, ya);
      }
      c.stroke();
      c.beginPath();
      for (var b = 0; b < n; b++) {
        var xb = (b / (n - 1)) * W;
        var yb = mid + buf[b] * amp;
        if (b === 0) c.moveTo(xb, yb); else c.lineTo(xb, yb);
      }
      c.stroke();
    },

    // 5. 环形：以画布中心为圆心向外的放射线
    radial: function (c, W, H) {
      var n = 96;
      var cx = W / 2, cy = H / 2;
      var r0 = Math.min(W, H) * 0.10;
      var r1 = Math.min(W, H) * 0.48;
      c.lineCap = 'round';
      for (var i = 0; i < n; i++) {
        var a = (i / n) * Math.PI * 2 - Math.PI / 2;
        var b = Math.floor((i / n) * BANDS);
        var v = level[b];
        var len = r0 + v * (r1 - r0);
        c.strokeStyle = rgba(CFG.color, 0.35 + 0.6 * v);
        c.lineWidth = Math.max(1, (2 * Math.PI * r1 / n) * 0.55) * dpr;
        c.beginPath();
        c.moveTo(cx + Math.cos(a) * r0, cy + Math.sin(a) * r0);
        c.lineTo(cx + Math.cos(a) * len, cy + Math.sin(a) * len);
        c.stroke();
      }
    },

    // 6. 折线：细线折线图，带网格
    line: function (c, W, H) {
      var n = 128;
      sample(n, buf);
      var pad = 6 * dpr;
      var gx = 8;
      c.strokeStyle = rgba(CFG.color, 0.12);
      c.lineWidth = 1 * dpr;
      for (var g = 1; g < gx; g++) {
        var y = pad + (H - pad * 2) * (g / gx);
        c.beginPath(); c.moveTo(0, y); c.lineTo(W, y); c.stroke();
      }
      c.beginPath();
      for (var i = 0; i < n; i++) {
        var x = (i / (n - 1)) * W;
        var y = H - pad - buf[i] * (H - pad * 2);
        if (i === 0) c.moveTo(x, y); else c.lineTo(x, y);
      }
      c.strokeStyle = rgba(CFG.color, 0.95);
      c.lineWidth = 1.8 * dpr;
      c.lineJoin = 'round';
      c.stroke();
    },

    // 7. 面积：填充到基线的面积图
    area: function (c, W, H) {
      var n = 128;
      sample(n, buf);
      var baseY = H - 2;
      c.beginPath();
      c.moveTo(0, baseY);
      for (var i = 0; i < n; i++) {
        c.lineTo((i / (n - 1)) * W, baseY - buf[i] * (H - 4));
      }
      c.lineTo(W, baseY);
      c.closePath();
      var g = c.createLinearGradient(0, H, 0, 0);
      g.addColorStop(0, rgba(CFG.color, 0.95));
      g.addColorStop(0.7, rgba(CFG.color, 0.5));
      g.addColorStop(1, rgba(CFG.color, 0.06));
      c.fillStyle = g;
      c.fill();

      c.beginPath();
      for (var j = 0; j < n; j++) {
        var x = (j / (n - 1)) * W;
        var y = baseY - buf[j] * (H - 4);
        if (j === 0) c.moveTo(x, y); else c.lineTo(x, y);
      }
      c.strokeStyle = rgba(CFG.color, 1);
      c.lineWidth = 1.6 * dpr;
      c.stroke();
    },

    // 8. 反射：柱体 + 下方倒影
    reflex: function (c, W, H) {
      var n = 64;
      var slot = W / n;
      var bw = Math.max(1, slot * 0.6);
      var axis = H * 0.52;
      var amp = axis - 4;
      var g = c.createLinearGradient(0, axis, 0, 0);
      g.addColorStop(0, rgba(CFG.color, 1));
      g.addColorStop(1, rgba(CFG.color, 0.45));
      c.fillStyle = g;
      for (var i = 0; i < n; i++) {
        var h = level[i] * amp;
        if (h < 0.7) continue;
        roundRect(c, i * slot + (slot - bw) / 2, axis - h, bw, h, Math.min(bw / 2, 2 * dpr));
      }
      var rg = c.createLinearGradient(0, axis, 0, H);
      rg.addColorStop(0, rgba(CFG.color, 0.42));
      rg.addColorStop(1, rgba(CFG.color, 0));
      c.fillStyle = rg;
      for (var j = 0; j < n; j++) {
        var h2 = level[j] * amp;
        if (h2 < 0.7) continue;
        var x2 = j * slot + (slot - bw) / 2;
        c.fillRect(x2, axis + 1, bw, h2 * 0.62);
      }
      c.fillStyle = rgba(CFG.color, 0.5);
      c.fillRect(0, axis, W, Math.max(1, dpr));
    }
  };

  var STYLE_LIST = [
    ['classic', '经典柱'], ['led', 'LED'], ['mirror', '镜像'], ['wave', '波形'],
    ['radial', '环形'], ['line', '折线'], ['area', '面积'], ['reflex', '反射']
  ];

  window.__vizStyles = STYLE_LIST;

  /* ---------- 画布与挂载 ---------- */

  var stage = null, canvas = null, ctx = null;
  var dpr = 1, W = 0, H = 0;

  function ensureStage() {
    var screen = document.querySelector('.lyrics-screen');
    var lyrics = document.querySelector('#lyrics');

    if (!screen && !lyrics) {
      if (stage) stage.style.display = 'none';
      return false;
    }

    if (!stage) {
      stage = document.createElement('div');
      stage.id = 'viz-stage';
      stage.className = 'viz-stage';
      canvas = document.createElement('canvas');
      stage.appendChild(canvas);
      ctx = canvas.getContext('2d');
    }

    // 真正的分层：挂在 .lyrics-screen 上（position:fixed，整窗大小），
    // 插在歌词那一栏之前。
    //
    //   · 直接子元素 -> left/right:0 + bottom:0 铺满整窗宽度、贴住窗底
    //   · 排在歌词之前 -> 同 z-index 时由 DOM 顺序决定上下，歌词在上
    //   · 舞台自带的那层 60% 深灰遮罩是 .lyrics-screen 的 background，
    //     画在所有子元素之下，所以频谱不会被压暗
    //
    // 注意不能挂到 #lyrics 自己的父节点上：那只是右侧歌词栏（约 1000px
    // 宽），频谱会被挤成一小块，跟着歌词一起滚走。
    var parent = screen || lyrics.parentNode;
    var anchor = null;
    if (lyrics) {
      // #lyrics 通常还裹着几层 div，向上找到 .lyrics-screen 的直接子节点
      var node = lyrics;
      while (node && node.parentNode !== parent) node = node.parentNode;
      if (node) anchor = node;
    }
    if (stage.parentNode !== parent || stage.nextSibling !== anchor) {
      if (anchor) parent.insertBefore(stage, anchor);
      else parent.appendChild(stage);
    }
    stage.style.display = '';
    stage.classList.add('on');
    stage.style.setProperty('--viz-h', CFG.height + 'px');
    stage.style.setProperty('--viz-op', String(CFG.opacity));
    resize();
    return true;
  }

  function resize() {
    if (!canvas) return;
    dpr = Math.min(2, window.devicePixelRatio || 1);
    var r = stage.getBoundingClientRect();
    var w = Math.max(1, Math.floor(r.width * dpr));
    var h = Math.max(1, Math.floor(r.height * dpr));
    if (w === W && h === H) return;
    W = canvas.width = w;
    H = canvas.height = h;
  }

  function draw() {
    if (!ctx || !W) return;
    ctx.clearRect(0, 0, W, H);
    if (!CFG.on) return;

    var fn = STYLES[CFG.style] || STYLES.mirror;

    // 光晕：同一形状再画一遍放大模糊的淡色副本。
    if (CFG.glow > 0.01) {
      ctx.save();
      ctx.globalAlpha = CFG.glow * 0.22;
      ctx.filter = 'blur(' + (7 * dpr) + 'px)';
      ctx.translate(0, H * 0.01);
      fn(ctx, W, H);
      ctx.restore();
    }

    fn(ctx, W, H);
  }

  /* ---------- 设置同步 ---------- */

  function reload() {
    var sig;
    try { sig = localStorage.getItem(KEY) || ''; } catch (e) { return; }
    if (sig === lastSig) return;
    lastSig = sig;
    try {
      var o = JSON.parse(sig);
      if (!o) return;
      Object.keys(CFG).forEach(function (k) { if (o[k] !== undefined) CFG[k] = o[k]; });
    } catch (e) {}
    if (stage) {
      stage.style.setProperty('--viz-h', CFG.height + 'px');
      stage.style.setProperty('--viz-op', String(CFG.opacity));
    }
    resize();
  }

  function syncColor() {
    try {
      var raw = localStorage.getItem('lyc_word_cfg');
      if (!raw) return;
      var o = JSON.parse(raw);
      if (o && o.sung && o.sung.toLowerCase() !== String(CFG.color).toLowerCase()) {
        CFG.color = o.sung;
      }
    } catch (e) {}
  }

  var n2 = 0;
  var lastSig = '';

  function tick() {
    requestAnimationFrame(tick);
    n2++;

    var now = performance.now();
    var dt = lastTick ? Math.min(0.05, (now - lastTick) / 1000) : 1 / 60;
    lastTick = now;

    animate(dt);
    if (ensureStage()) draw();
    else if (stage) { stage.style.display = 'none'; }

    if (n2 % 30 === 0) { syncColor(); reload(); }
  }

  reload();

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', tick);
  } else {
    tick();
  }

  window.addEventListener('resize', function () { W = H = 0; resize(); });

  log('ready, styles=' + STYLE_LIST.map(function (s) { return s[0]; }).join(','));
})();