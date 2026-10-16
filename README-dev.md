# audio-viz

全屏歌词页的音乐频谱，**真实音频驱动**，跟着声音走。

## 数据从哪来

native host 用 Windows WASAPI 环回抓系统正在播放的声音，做 2048 点 FFT
（Hann 窗），整理成 64 个对数频段，以 8 ms 目标帧间隔推给前端。
完全在**系统层捕获**，不碰播放链路 —— 音频质量和音量都不受影响。

**不要改成页面内捕获。** `<audio>` + `createMediaElementSource` 会改变音频通路，
跨域音源直接静音。

## 输出设备：全都听，每帧挑有声的那个

这是这个插件最难对付的一块。同一个音频会话在这类机器上要同时服务好几个渲染端点，
而且每个坏法都不一样：

| 设备 | 坏法 |
|---|---|
| FxSound（虚拟，默认） | 短探测能过，之后永久断流 —— 回调还在触发，`BytesRecorded` 恒为 0 |
| Realtek（物理那个） | 字节量健康，但内容全是 0，因为 FxSound 占着音频图不放行 |
| 耳机 | 真正的信号在这里 |

所以**事先挑一个设备是不成立的**：挑中的那个可能是两个坏设备里的任何一个，
从外面根本分不出来。

做法是**同时对所有活动的渲染端点开环回**，每个端点一个独立的环形缓冲，
每帧比较谁的信号最强就拿谁去做 FFT。设备死了只是不再获胜，不用重新选择；
音频在设备之间搬家（拔耳机、关掉 FxSound）下一帧就跟上。

判定一个端点是不是真死了，条件是**别的端点正在出声、而它一个字节都不给**。
系统整体安静（暂停、换歌间隙）时所有端点都没数据，那是安静不是故障，
这时不能丢端点 —— 否则会陷入「丢掉 → 重开 → 还是没数据 → 再丢」的空转。

顺带一提：`FxSound` 这类虚拟设备卡死时，重启它进程就能恢复音频链路
（路径 `C:\Program Files\FxSound LLC\FxSound\FxSound.exe`）。它长期运行、
CPU 占用高的时候值得先试这一步。

## 频段计算

- **保持短 FFT，补偿低分辨率。** 2048 点在 48kHz 下每 bin 23.4Hz，窗口中心
  滞后约 21ms；加倍窗口会让整个显示平白多约 21ms 的中心延迟。64 个对数显示段
  不可能各自占有独立 FFT bin，所以 1.4.1 把每个真实 bin 按对数频率位置分配给相邻
  显示段，并对没有原生 bin 的段做邻带插值，不再通过向前推宽 bin 边界来硬凑段数。
  最低和最高范围外侧的 bin 归到首尾段，保证目标范围每段有读数。
- **逐声道做 FFT，再合并功率。** 不要先对左右声道做带符号平均：左右反相时会
  互相抵消，让真实存在的频率完全消失。当前每个声道独立计算频段能量，再对功率
  取均值；普通立体声电平保持一致，反相内容也不会被抹掉。
- **用能量积分**（`sqrt(Σ|X|²)`）而不是幅度均值。取均值会让宽的高频带
  比窄的低频带显得更安静 —— 早期版本为此不得不加一条假的
  `v *= 1 + 0.55*(b/N)` 高频补偿，那不是真实频谱，只是把失真盖住。
  改用能量积分后补偿可以整个删掉。
- **AGC（自动增益）锚在 96 百分位，不是绝对最大值。** 一声口哨或一次镲片崩溃
  会把绝对最大值拉到远超音乐主体的高度，锚在它上面会让所有正常频段全部趴到底。
  锚在音乐本体上，安静的民谣和重制的母带都能填满显示。
- 固定 dB 窗口没法同时伺候所有歌：安静的现场录音和响的母带会落在完全不同的地方，
  其中一个必然要么是一条平线、要么是一堵削平的墙。

## 八种样式

参考 `hvianna/audioMotion-analyzer` 的官方模式集：
经典柱 / LED 点阵 / 镜像 / 波形 / 环形 / 折线 / 面积 / 反射。
`word-lyric` 的设置面板里是 4×2 的网格切换（八个选项用下拉框没法一眼扫完），
两边各存一份模式名列表，改动时要同步。

样式列表：`content.js` 的 `STYLES` / `STYLE_LIST`，
`word-lyric/content.js` 的 `VIZ_STYLES`。

## 手感

「动作生硬」不是插值系数的问题，是三件事：

- **帧率无关的指数平滑** `level += (target-level) * (1 - e^(-dt/tau))`。
  按帧算的插值在 60fps 和 30fps 下速度不一样，掉帧时整条频谱会「变慢」。
- **分开上升和下行 tau**：上升恒快，避免漏掉瞬态；下行才随柔和度调长，避免高频掉得太急。
- **峰值两段式**：先保持 `peakHold` 毫秒，再按 `peakFade` 淡出。
  只有匀速回落的峰值看起来是机械的。

这三项对应面板上的「柔和度」滑块和代码里的 `peakHold` / `peakFade`。

## 挂在哪一层

canvas 挂在 `.lyrics-screen` 里、歌词那一栏**之前**（见 `content.js` 的
`ensureStage`）。于是层级是：

```
.lyrics-screen        position:fixed，整窗大小
                      background 是那层 60% 深灰遮罩
  ├ .bg-layer …      z=-1，专辑图
  ├ .viz-stage       ← 频谱
  ├ 歌词栏           ← 同 z-index 时靠 DOM 顺序在上
  ├ button.close-btn z=99
  └ 歌词设置面板      z=101
```

遮罩是 `.lyrics-screen` 自己的 `background`，画在所有子元素之下，
所以频谱既不会被压暗，又在歌词之下 —— 这就是老板要的「真正的分层」。

注意**不能**挂到 `#lyrics` 自己的父节点上：那只是右侧歌词栏（约 1000px 宽），
频谱会被挤成一小块、还跟着歌词一起滚走。

用 `position:absolute` 而不是 `fixed`，底边就永远贴着歌词页的底边，
不会因为播放器、控制条、任务栏的高度变化而悬空。

## 构建

```powershell
csc /nologo /target:exe /platform:anycpu /optimize+ `
    /r:host\NAudio.dll /out:host\spectrum-sink.exe host\Spectrum.cs
```

`NAudio.dll` 必须和 exe 同目录。源码是纯 ASCII，原因见根目录 README。

## 文件

```
host/Spectrum.cs        多端点监听 + FFT + 频段映射 + AGC
host/spectrum-sink.exe  编译产物
host/NAudio.dll         NAudio（MIT，不随本仓库分发）
bridge.html / bridge.js 原生宿主通道
content.js              八种样式的渲染与柔性动画
background.js
styles.css
manifest.json
```

## 排查

`spectrum.log`（在 `%APPDATA%\moekoemusic\`）是 exe 的运行日志。
它会打三类行：

- `tap listening to <设备>` —— 正在监听哪些端点
- `tap silent while others carry audio: <设备>, dropping it` —— 判定死设备
- `pick following audio on <设备>` / `pick no audible signal yet` —— 每帧选了谁
- `frames=N rms=… taps=N src=…` —— 每 100 帧一次的总览

对照关系：

| 现象 | 原因 |
|---|---|
| 没有任何 `tap listening to` | 没有活动的渲染端点，或权限不足 |
| `taps=1` 且 `rms` 一直 0 | 唯一活着的端点只给零字节（Realtek 在 FxSound 手里就是这个状态） |
| `rms` 一直 0 但 `pick` 说在跟某个设备 | 那个设备有声，只是 FxSound 卡死了 —— 先重启 FxSound |
| `rms` 正常但画面不动 | 前端没收到消息，查 bridge |
| 画面动但柱子不动 | 检查 `h` 的计算，`level` 存的是 0~1，算柱高时**不能再除以 255**（这个 bug 踩过一次，症状是只剩峰值帽可见） |
