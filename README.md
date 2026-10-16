# MoeKoe Music 音乐频谱插件

全屏歌词页的音乐频谱，八种样式。

Windows WASAPI 环回捕获真实音频，2048 点 FFT，同时监听所有输出设备。1.4.1 起逐声道分析并合并能量，避免左右声道反相时相互抵消；频段按 FFT bin 的实际对数位置映射，并对无独立 bin 的显示频段插值补齐。歌词设置面板内容可在屏幕内滚动。

## 安装

把整个仓库放进：

```
%APPDATA%\moekoemusic\extensions\
```

然后重启 MoeKoe Music，在 **设置 → 插件 → 刷新插件** 里启用。

**需要插件自带原生宿主。** 首次启动时会自动拉起，界面右下角会显示本地程序状态。

二进制已随仓库提交 —— MoeKoe 按路径加载、没有构建步骤，所以 clone 下来即可用。

## 更多信息

- [`COMPATIBILITY.md`](COMPATIBILITY.md) —— 依赖应用的哪些类名、设置键和原生接口。
- [`README-dev.md`](README-dev.md) —— 架构和开发注意事项。
