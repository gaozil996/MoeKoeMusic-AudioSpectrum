using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Latency harness click player -- plays, timestamps, exits.
//
// "1s silence + burst + 1s silence + burst + 1s silence" is built in
// memory and pushed to the default render endpoint through WasapiOut,
// shared mode, fully prebuffered. The wall-clock instant of Play() goes
// to stdout as a 13-digit unix millisecond value; latency-test.js reads
// it as the expected-origin clock.
//
// Why not SoundPlayer: it sits on winmm, whose output buffer starts
// ~100ms before the first sample moves, and that number wanders by tens
// of ms between runs. It was the single largest term in the harness and
// it measured nothing about this plugin. WasapiOut + prebuffered samples
// starts within one device period of Play() -- Measure.cs established
// the same path at 47ms out->loopback on this machine.
//
// Why a 40ms full-scale noise burst instead of a 3-sample click: in the
// host's log-frequency FFT, an impulse spreads its energy flat and lands
// ~40dB below a musical note in any given bin -- while the user's music
// plays, the click simply does not rise above the floor (window peaks
// came out at 1.06-1.25x the window median, i.e. nothing detectable).
// A burst filling more than one full FFT window puts real energy into
// every band at once, which is what the peak-vs-median detector needs.
static class ClickPlay
{
    const int SR = 48000;
    const int CLICK_AT = SR;                // first burst: 1.000 s
    const int CLICK_AT2 = 2 * SR;           // second burst: 2.000 s
    const int BURST = (int)(SR * 0.040);    // 40 ms > one 2048-sample window

    static void Main()
    {
        int n = (int)(SR * 3.0);
        var b = new byte[n * 4];

        // Deterministic full-scale noise (xorshift32) so runs are comparable.
        uint st = 0x9E3779B9;
        var noise = new float[BURST];
        for (int i = 0; i < BURST; i++)
        {
            st ^= st << 13; st ^= st >> 17; st ^= st << 5;
            noise[i] = ((st & 0xFFFF) / 32767.5f) - 1.0f;
        }

        foreach (int at in new int[] { CLICK_AT, CLICK_AT2 })
            for (int i = 0; i < BURST; i++)
                BitConverter.GetBytes(noise[i]).CopyTo(b, (at + i) * 4);

        var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var bf = new BufferedWaveProvider(new WaveFormat(SR, 32, 1));
        bf.ReadFully = false;
        bf.AddSamples(b, 0, b.Length);

        var wo = new WasapiOut(dev, AudioClientShareMode.Shared, false, 40);
        wo.Init(bf);

        wo.Play();
        Console.WriteLine(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Console.Out.Flush();

        Thread.Sleep(3500);
        try { wo.Stop(); } catch { }
        try { wo.Dispose(); } catch { }
        Environment.Exit(0);
    }
}
