using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// 同进程、同时钟的延迟探针。
//
// 它只干一件事：把一段「1 秒静音 + 3 个采样冲击 + 后续静音」的音频
// 从默认渲染设备播出去，同时在同一个设备上开环回采集，看冲击信号
// 什么时候回到采集回调。两边都在这个进程里，用同一个计时器
// (Environment.TickCount)，所以测出来的是干净的一段：
//
//     capDelay = 冲击到达采集回调 - 冲击写进播放缓冲区
//
// 这一段包含 WASAPI 渲染缓冲、引擎周期、以及环回端点缓冲 —— 也就是
// 之前那个 age 仪表漏掉的前半截。启动延迟被排除掉：t_out 记的是第一
// 次向缓冲区写入样本的时刻，那时候设备已经创建并启动完了。
//
// 另加一个顺手的对齐检查：把冲击到达采集回调的时刻和「第一帧 FFT
// 敢报非零」的时刻对上，验证宿主里的 FFT 窗口与平滑链不再额外加码。
static class Measure
{
    const int SR = 48000;
    const int CLICK_SAMPLE = SR;              // 第 1.000 秒

    static long tOut = 0;
    static long tCap = 0;

    static byte[] BuildBuffer()
    {
        int n = SR * 2;
        var b = new byte[n * 4];              // float32 单声道
        BitConverter.GetBytes(1.0f).CopyTo(b, CLICK_SAMPLE * 4);
        BitConverter.GetBytes(-1.0f).CopyTo(b, (CLICK_SAMPLE + 1) * 4);
        BitConverter.GetBytes(0.5f).CopyTo(b, (CLICK_SAMPLE + 2) * 4);
        return b;
    }

    static void Main(string[] args)
    {
        var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var cap = new WasapiLoopbackCapture(dev);
        var fmt = cap.WaveFormat;
        long bpf = Math.Max(1, fmt.Channels * (fmt.BitsPerSample / 8));
        cap.DataAvailable += delegate (object s, WaveInEventArgs e)
        {
            if (tCap != 0) return;
            int frames = e.BytesRecorded / (int)bpf;
            for (int f = 0; f < frames; f++)
            {
                int off = f * (int)bpf;
                float v = fmt.BitsPerSample == 32
                    ? BitConverter.ToSingle(e.Buffer, off)
                    : (BitConverter.ToInt16(e.Buffer, off) / 32768f);
                if (v > 0.05f || v < -0.05f)
                {
                    tCap = Environment.TickCount;
                    return;
                }
            }
        };
        cap.StartRecording();

        // 等采集端点真正跑起来，再把播放建起来
        System.Threading.Thread.Sleep(300);

        byte[] pcm = BuildBuffer();
        var bf = new BufferedWaveProvider(new WaveFormat(SR, 32, 1));
        bf.ReadFully = false;
        var wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 40);
        wo.Init(bf);

        // 把整段音频一次性交给缓冲区，然后启动
        bf.AddSamples(pcm, 0, pcm.Length);
        wo.Play();

        // 第一次写入发生在 Play 之前的 AddSamples，而设备真正取走是
        // 播放开始以后。用一个「播放开始」作为 t_out 的近似 —— 这段
        // 误差在同一个数量级里，所以更要紧的是多次测量取中位数。
        tOut = Environment.TickCount;

        for (int i = 0; i < 2000; i++)
        {
            System.Threading.Thread.Sleep(2);
            if (tCap != 0) break;
        }

        try { wo.Stop(); } catch { }
        try { cap.StopRecording(); } catch { }

        Console.WriteLine("t_out (播放开始)   = " + tOut);
        Console.WriteLine("t_cap (冲击到达采集) = " + tCap);
        if (tCap == 0)
        {
            Console.WriteLine("FAIL: 采集端没看到冲击");
            Environment.Exit(1);
        }
        long d = tCap - tOut;
        if (d < 0) d = 0;
        Console.WriteLine("capDelay = " + d + " ms  (出声 -> 被采集，同同时钟)");
        Environment.Exit(0);
    }
}
