using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/*
    Spectrum sink for MoeKoe.

    Captures whatever the system is playing through WASAPI loopback,
    runs an FFT, and streams 64 logarithmic frequency bands to stdout
    as JSON. It never touches the playback path, so audio is untouched.

    Two hard-won details:
      1. Render endpoints lie. On this class of machine a single audio
         session has to serve three of them, and each fails differently:

           FxSound (virtual, default)  can pass a short probe and then go
                                       permanently quiet: the callback keeps
                                       firing but BytesRecorded stays 0
           Realtek (the physical one)  streams a healthy amount of bytes
                                       that are all zeros, because FxSound
                                       owns the graph and never forwards
           Headset                     carries the real signal

         So picking one device up front is not survivable: whichever we pick
         can be one of the two dead ones, and there is no way to tell from
         the outside which is which. Instead we open a loopback capture on
         EVERY active render endpoint at once and, each frame, run the FFT on
         whichever one currently carries the most signal. A device that dies
         just stops winning; nothing has to be re-selected, and audio moving
         between endpoints (headset unplugged, FxSound toggled off) is picked
         up on the next frame.

      2. All source text is plain ASCII: csc reads this file with the
         system codepage and mis-parses non-ASCII inside string
         literals.
*/

static class Program
{
    // Thread.Sleep rounds up to the system timer tick, which is ~15.6 ms
    // unless somebody asks for finer. timeBeginPeriod(1) makes the tick 1 ms
    // process-wide while we run, so FRAME_MS=8 really sleeps ~8 ms instead of
    // 15.6. Without this the frame loop can never beat the tick no matter
    // what number we put in Sleep, and the frame interval is a visible third
    // of the lag budget. Browsers and media players do exactly this.
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint uPeriod);

    // Latency budget, biggest term first, after measuring rather than guessing.
    //
    // The `age=` field in the frame log is the measured age of the newest
    // audio sample in the ring when the frame was computed. With the loopback
    // tap running and music playing it reads 0-30 ms, so capture is NOT the
    // problem -- that was worth checking, because a 100 ms NAudio endpoint
    // buffer is the usual suspect and the bundled 1.10 turns out not to do
    // that. It initialises to the device period, roughly one 10 ms period.
    //
    // What is left is the analysis window plus three smoothing tails:
    //   2048 @ 48 kHz  ->  42.7 ms window, ~21 ms of centre age
    //   4096 @ 48 kHz  ->  85.3 ms window, ~43 ms of centre age
    // The window length IS the lag: a spectrum cannot describe audio that has
    // not been recorded yet. 2048 is close to the physical floor -- 100 Hz
    // has a 10 ms period, so even one cycle of the lowest useful band needs
    // half of a 2048 window.
    //
    // 2048 used to be rejected here because the narrow log-spaced bands then
    // spanned 1-2 bins, whose magnitudes jump frame to frame and read as
    // noise, leaving the low half of the display empty. Widening the bands
    // (raising MIN_BINS) cannot fix that -- a band made wider still only
    // holds the same 1-2 bins' worth of energy. The fix is averaging over
    // time, which is what the inline average in EmitFrame does below. Resolution is bought with a few
    // frames of history instead of a longer window, because frames of history
    // cost latency only in proportion and can be made band-dependent.
    const int FFT_SIZE = 2048;
    const int BANDS = 64;
    const int MIN_BINS = 2;       // every band must span at least this many bins

    // Temporal averaging now lives inline in EmitFrame, gated per band by how
    // many bins it actually spans (see the "thin band" note there). No global
    // ramp constant is needed: the criterion is bins-per-band, not frequency.

    // Smoothing on the mapped 0..1 bar value, split by direction. The attack is
    // a sub-frame time constant, because that is the only side the eye reads as
    // "lagging the music". The release is the opposite: it is what the 柔和度
    // slider is supposed to govern, and a fast stage here caps it no matter what
    // the renderer asks for. 0.45 at FRAME_MS was a ~15 ms fall, i.e. the bar hit
    // the floor in one or two frames -- the slider could only add a few ms on top
    // of that. 0.10 puts this stage at ~70 ms, so the renderer's 26..170 ms is
    // what actually sets the tail.
    const double SMOOTH_UP = 0.85;
    const double SMOOTH_DOWN = 0.10;
    const int MAX_MSG = 60 * 1024;
    const int FRAME_MS = 8;
    const double DB_RANGE = 46.0;     // visible dynamic range, in dB
    const double GATE_DB = 24.0;      // how far below the loudest band content may sit
    const double GATE_SLOPE = 3.0;    // dB of attenuation per dB past the gate
    const double DB_HEADROOM = 8.0;   // keep the loudest band off the ceiling
    const double AGC_ATTACK = 1.0;    // track a rise instantly
    const double AGC_RELEASE = 0.020; // fall back slowly, so it never pumps
    const double AGC_MIN_DB = -70.0;  // never amplify past this
    const double SILENT_RMS = 0.0012; // below this the frame is treated as silence

    static object WriteLock = new object();

    struct Cpx
    {
        public double X;
        public double Y;
    }

    static void Send(string json)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(json + "\n");
            if (b.Length > MAX_MSG) return;
            lock (WriteLock) { Console.Out.WriteLine(json); Console.Out.Flush(); }
        }
        catch { }
    }

    static void Note(string tag, string msg)
    {
        string clean = (msg == null ? "" : msg)
            .Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", "").Replace("\n", " ");
        Send("{\"diag\":\"" + tag + "\",\"msg\":\"" + clean + "\"}");
        HostLog(tag + " " + clean);
    }

    static string CacheDir()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\moekoemusic"; }
        catch { return null; }
    }

    static void HostLog(string msg)
    {
        try
        {
            string dir = CacheDir();
            if (dir == null) return;
            var fs = new FileStream(dir + "\\spectrum.log", FileMode.Append, FileAccess.Write,
                                    FileShare.ReadWrite | FileShare.Delete);
            try
            {
                var bytes = Encoding.UTF8.GetBytes("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }
            finally { fs.Dispose(); }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // FFT: in-place radix-2 Cooley-Tukey. n must be a power of two.
    // ------------------------------------------------------------------
    static void Fft(Cpx[] a, int n)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { Cpx t = a[i]; a[i] = a[j]; a[j] = t; }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double curR = 1.0, curI = 0.0;
                for (int j = 0; j < half; j++)
                {
                    Cpx u = a[i + j];
                    Cpx v = a[i + j + half];
                    double vr = v.X * curR - v.Y * curI;
                    double vi = v.X * curI + v.Y * curR;
                    a[i + j] = new Cpx { X = u.X + vr, Y = u.Y + vi };
                    a[i + j + half] = new Cpx { X = u.X - vr, Y = u.Y - vi };
                    double nr = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nr;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Endpoint taps
    // ------------------------------------------------------------------
    // One loopback capture per active render endpoint, each with its own
    // ring buffer. The frame loop picks the tap that currently carries the
    // most signal; everything else is simply ignored that frame.
    class Tap
    {
        public MMDevice Dev;
        public string Name;
        public WasapiLoopbackCapture Cap;

        public float[][] Rings;
        public int RingPos;
        public int RingLen;

        public int SampleRate;
        public int Bps;
        public int Bpf;
        public int Ch;
        public bool IsFloat;

        // Recent signal strength, decayed every frame so it tracks "loud at
        // this moment" rather than "loud at some point since launch".
        public double Peak;
        public long Bytes;
        public int DeadStrikes;
        public long LastByteAt;
        public volatile bool Broken;
    }

    static List<Tap> taps = new List<Tap>();
    static object TapLock = new object();

    // Endpoints that turned out to be dead: not reopened for a while, so a
    // wedged virtual device does not get hammered every refresh.
    static Dictionary<string, long> blocked = new Dictionary<string, long>();
    const long BLOCK_MS = 30000;

    // Harness mode (--default): capture ONLY the default render endpoint.
    // The loudest-tap picker is right for the app, which must follow
    // per-app routing, but a latency probe that plays its click into the
    // default device cannot tolerate the winner flip-flopping between
    // endpoints mid-measurement -- the click then lands in a ring buffer
    // nobody is draining and the whole measurement silently turns to music.
    static bool DefaultOnly = false;

    static List<MMDevice> RenderEndpoints()
    {
        var all = new List<MMDevice>();
        try
        {
            var en = new MMDeviceEnumerator();
            try
            {
                var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                all.Add(def);
            }
            catch { }
            if (DefaultOnly) return all;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool dup = false;
                foreach (var x in all) if (x.ID == d.ID) dup = true;
                if (!dup) all.Add(d);
            }
        }
        catch { }
        return all;
    }

    static Tap OpenTap(MMDevice dev)
    {
        var t = new Tap();
        t.Dev = dev;
        t.Name = dev.FriendlyName;
        var cap = new WasapiLoopbackCapture(dev);
        // NOTE on buffer size: the bundled NAudio is 1.10, where
        // WasapiLoopbackCapture only offers (MMDevice) -- there is no
        // BufferMilliseconds property and no ctor overload that takes one, so
        // the endpoint buffer cannot be shrunk from here. What it actually
        // initialises to is the device's AudioClientPeriodSize, roughly one
        // period (~10 ms at 48 kHz).
        //
        // The age field in the frame log is there so this file stops guessing
        // about where the lag lives. Measure, then fix the term that is
        // actually large.
        var fmt = cap.WaveFormat;
        t.SampleRate = fmt.SampleRate;
        t.Ch = Math.Max(1, fmt.Channels);
        t.Rings = new float[t.Ch][];
        for (int c = 0; c < t.Ch; c++) t.Rings[c] = new float[FFT_SIZE];
        t.Bps = Math.Max(1, fmt.BitsPerSample / 8);
        t.Bpf = Math.Max(1, t.Ch * t.Bps);
        t.IsFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        cap.DataAvailable += delegate (object s, WaveInEventArgs e) { OnTapAudio(t, e); };
        cap.StartRecording();
        t.Cap = cap;
        return t;
    }

    // Bring the tap list in line with the endpoints that exist right now.
    static void RefreshTaps()
    {
        var now = Environment.TickCount;
        var want = RenderEndpoints();

        // A quiet system is not a dead endpoint. Only call a tap dead when
        // some OTHER tap is demonstrably carrying audio right now: then a
        // channel delivering nothing is genuinely wedged, whereas with music
        // paused every endpoint is quiet and dropping taps would only churn.
        bool audioSomewhere = false;
        foreach (var t in taps) if (t.Peak > 0.0005) audioSomewhere = true;

        lock (TapLock)
        {
            var kept = new List<Tap>();
            foreach (var t in taps)
            {
                bool gone = false;
                foreach (var d in want) if (d.ID == t.Dev.ID) gone = false;

                // A tap that never receives a byte while others are carrying
                // sound is a wedged virtual endpoint. Drop it, but block it
                // briefly so we do not immediately reopen the same dead
                // channel on the next pass.
                if (audioSomewhere && t.DeadStrikes >= 40 && now - t.LastByteAt > 3000)
                {
                    gone = true;
                    blocked[t.Dev.ID] = now;
                    Note("tap", "silent while others carry audio: " + t.Name
                        + ", dropping it");
                }

                if (gone)
                {
                    CloseTap(t);
                }
                else kept.Add(t);
            }
            taps = kept;

            foreach (var d in want)
            {
                bool have = false;
                foreach (var t in taps) if (t.Dev.ID == d.ID) have = true;
                if (have) continue;

                long until;
                if (blocked.TryGetValue(d.ID, out until) && now < until) continue;

                try
                {
                    var t = OpenTap(d);
                    taps.Add(t);
                    Note("tap", "listening to " + t.Name + " (" + t.SampleRate + "Hz x"
                        + t.Ch + " " + (t.Bps * 8) + "bit)");
                }
                catch (Exception ex)
                {
                    Note("tap", "cannot open " + d.FriendlyName + ": "
                        + ex.GetType().Name);
                }
            }
        }
    }

    static void CloseTap(Tap t)
    {
        try { t.Cap.StopRecording(); } catch { }
        try { t.Cap.Dispose(); } catch { }
        t.Broken = true;
    }

    // ------------------------------------------------------------------
    // Streaming loop
    // ------------------------------------------------------------------
    static Cpx[] buf = new Cpx[FFT_SIZE];
    static int[] binBand0 = new int[FFT_SIZE / 2 + 1];
    static int[] binBand1 = new int[FFT_SIZE / 2 + 1];
    static double[] binMix1 = new double[FFT_SIZE / 2 + 1];
    static double[] bandCoverage = new double[BANDS];
    static float[] smooth = new float[BANDS];
    static float[] peak = new float[BANDS];
    static double[] bandDb = new double[BANDS];
    // Time-averaged band level. Averaging here rather than widening the bands
    // is what makes the short window usable: a 1-bin band averaged over a few
    // frames reads as steady, the same bin not averaged does not.
    static double[] bandAvg = new double[BANDS];
    static bool bandAvgPrimed = false;
    static double agcCeil = -24.0;     // running loudest band, in dB
    static double agcFloor = -78.0;
    // 0, not 48000. The band edges are derived from the sample rate, so
    // seeding this with a real rate makes "endpoint is also 48000" look the
    // same as "edges were never built". The edges then stay all zero, every
    // band sums over an empty range and reads -180 dB, and the display is a
    // flat line no matter how loud the music is.
    static int sampleRate = 0;

    static void Main(string[] args)
    {
        foreach (string a in args) if (a == "--default") DefaultOnly = true;
        timeBeginPeriod(1);
        Send("{\"type\":\"hello\",\"host\":\"spectrum-sink\"}");
        Note("boot", "spectrum sink start");

        var pump = new Thread(Stream);
        pump.IsBackground = true;
        pump.Start();

        // A native host here is one-way: we only push stdout.
        // Reading stdin is a trap -- when stdin is not redirected
        // ReadLine returns null immediately, Main returns, and the
        // background pump dies with the process.
        Thread.Sleep(Timeout.Infinite);
    }

    static void Stream()
    {
        // Two jobs, on separate threads:
        //   supervisor  keeps the tap list matched to the real endpoints
        //   frames      picks the loudest tap and emits a spectrum
        RefreshTaps();
        var pump = new Thread(Pump);
        pump.IsBackground = true;
        pump.Start();

        while (true)
        {
            RefreshTaps();
            Thread.Sleep(4000);
        }
    }

    // Emit one spectrum every FRAME_MS, from whichever tap is loudest.
    static void Pump()
    {
        string lastWinner = "";
        int tick = 0;

        while (true)
        {
            Tap best = null;
            int n = 0;
            lock (TapLock)
            {
                n = taps.Count;
                for (int i = 0; i < n; i++)
                {
                    var t = taps[i];
                    // Decay so the ranking reflects the last few frames rather
                    // than the loudest moment since launch.
                    t.Peak *= 0.90;
                    if (best == null || t.Peak > best.Peak) best = t;
                }
            }

            if (best != null && best.Name != lastWinner)
            {
                lastWinner = best.Name;
                Note("pick", best.Peak > 0.0005
                    ? "following audio on " + best.Name
                    : "no audible signal yet (best is " + best.Name + ")");
            }

            EmitFrame(best);

            if (++tick % 100 == 0)
                HostLog("frames=" + frame + " rms=" + frameRms.ToString("F4")
                    + " taps=" + n + " src=" + (lastWinner.Length > 0 ? lastWinner : "-")
                    // Only meaningful while audio is actually flowing. During
                    // silence no samples arrive, so LastByteAt goes stale and
                    // the age reads tens of seconds -- a measurement artefact,
                    // not latency.
                    + " age=" + (frameRms >= SILENT_RMS ? frameAgeMs.ToString() : "off")
                    + "ms peak=" + frameAgeMax + "ms");
            Thread.Sleep(FRAME_MS);
        }
    }

    static void OnTapAudio(Tap t, WaveInEventArgs e)
    {
        try
        {
            if (t.Broken) return;

            byte[] raw = e.Buffer;
            int frames = e.BytesRecorded / t.Bpf;
            int limit = Math.Min(raw.Length, e.BytesRecorded);
            if (frames * t.Bpf > limit) frames = limit / t.Bpf;

            if (e.BytesRecorded > 0)
            {
                t.Bytes += e.BytesRecorded;
                t.LastByteAt = Environment.TickCount;
                t.DeadStrikes = 0;
            }
            else t.DeadStrikes++;
            if (frames <= 0) return;

            double peak = t.Peak;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * t.Bpf;
                for (int c = 0; c < t.Ch; c++)
                {
                    float sample = DecodeSample(raw, baseIdx + c * t.Bps, t.Bps, t.IsFloat);
                    t.Rings[c][t.RingPos] = sample;
                    double a = sample < 0 ? -sample : sample;
                    if (a > peak) peak = a;
                }
                t.RingPos = (t.RingPos + 1) & (FFT_SIZE - 1);
                if (t.RingLen < FFT_SIZE) t.RingLen++;
            }
            t.Peak = peak;
        }
        catch { t.Broken = true; }
    }

    static float DecodeSample(byte[] raw, int off, int bps, bool isFloat)
    {
        if (off + bps > raw.Length) return 0f;
        if (isFloat && bps == 4)
        {
            float f = BitConverter.ToSingle(raw, off);
            return float.IsNaN(f) || float.IsInfinity(f) ? 0f : f;
        }
        if (bps == 2) return BitConverter.ToInt16(raw, off) / 32768f;
        if (bps == 1) return (sbyte)raw[off] / 128f;
        if (bps == 4) return BitConverter.ToInt32(raw, off) / 2147483648f;
        if (bps == 3)
        {
            int i24 = raw[off] | (raw[off + 1] << 8) | (raw[off + 2] << 16);
            if ((i24 & 0x800000) != 0) i24 |= unchecked((int)0xFF000000);
            return i24 / 8388608f;
        }
        return 0f;
    }

    // ------------------------------------------------------------------
    // Band analysis + serialisation, fed from one tap
    // ------------------------------------------------------------------
    static void EmitFrame(Tap t)
    {
        try
        {
            int n = 0;
            float[][] src = null;
            int channels = 0;
            int pos = 0;
            lock (TapLock)
            {
                if (t != null && !t.Broken && t.RingLen >= FFT_SIZE)
                {
                    src = t.Rings; channels = t.Ch; pos = t.RingPos; n = FFT_SIZE;
                    if (t.SampleRate != sampleRate) { sampleRate = t.SampleRate; BuildBandMap(sampleRate); }
                }
            }

            double energy = 0;
            if (n == FFT_SIZE)
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < FFT_SIZE; i++)
                    {
                        double v = src[c][(pos + i) & (FFT_SIZE - 1)];
                        energy += v * v;
                    }
            frameRms = n == FFT_SIZE && channels > 0 ? Math.Sqrt(energy / (FFT_SIZE * channels)) : 0;
            frame++;

            if (t != null && t.LastByteAt > 0)
            {
                int age = (int)(Environment.TickCount - t.LastByteAt);
                if (age < 0) age = 0;
                frameAgeMs = age;
                if (age > frameAgeMax) frameAgeMax = age;
            }

            if (window == null) BuildWindow();

            double frameMaxDb = -200.0;
            if (n == FFT_SIZE)
            {
                Array.Clear(bandPower, 0, BANDS);
                for (int c = 0; c < channels; c++)
                {
                    // Oldest sample first. RingPos points at the next slot to write.
                    for (int i = 0; i < FFT_SIZE; i++)
                    {
                        float s = src[c][(pos + i) & (FFT_SIZE - 1)];
                        buf[i].X = s * window[i];
                        buf[i].Y = 0;
                    }
                    Fft(buf, FFT_SIZE);

                    // Add each channel's power independently. Averaging signed
                    // samples before FFT loses anti-phase stereo content.
                    for (int i = 1; i < FFT_SIZE / 2; i++)
                    {
                        int b0 = binBand0[i];
                        if (b0 < 0) continue;
                        double re = buf[i].X, im = buf[i].Y;
                        double power = re * re + im * im;
                        double w1 = binMix1[i];
                        bandPower[b0] += power * (1.0 - w1);
                        int b1 = binBand1[i];
                        if (b1 != b0) bandPower[b1] += power * w1;
                    }
                }

                // Short FFT windows cannot provide a unique bin for every
                // display band, especially at 48 kHz. Interpolate empty bands
                // from their neighbours rather than outputting false silence.
                for (int b = 0; b < BANDS; b++)
                {
                    if (bandCoverage[b] > 0) continue;
                    int left = b - 1, right = b + 1;
                    while (left >= 0 && bandCoverage[left] <= 0) left--;
                    while (right < BANDS && bandCoverage[right] <= 0) right++;
                    if (left < 0 && right >= BANDS) continue;
                    if (left < 0) bandPower[b] = bandPower[right];
                    else if (right >= BANDS) bandPower[b] = bandPower[left];
                    else
                    {
                        double f = (double)(b - left) / (right - left);
                        bandPower[b] = bandPower[left] + (bandPower[right] - bandPower[left]) * f;
                    }
                }

                for (int b = 0; b < BANDS; b++)
                {
                    // Average power across channels so normal stereo stays at
                    // the same level while phase-opposed channels no longer cancel.
                    double amp = Math.Sqrt(bandPower[b] / Math.Max(1, channels)) / (FFT_SIZE * 0.25);
                    double db = 20.0 * Math.Log10(amp + 1e-9);
                    bandDb[b] = db;
                    if (db > frameMaxDb) frameMaxDb = db;
                }

                // Time-average, but ONLY the bands that are actually thin. At 2048/48 kHz
                // one bin is 23.4 Hz, and the narrowest bands at the bottom of the log
                // scale span 2-4 bins -- those jump around frame to frame and read as
                // noise. Everything above 4 bins is already stable, and averaging it too
                // would just stack another low-pass on the whole display for nothing.
                // Priming is outright on the first frame so a song change does not fade
                // in from stale history.
                //
                // Asymmetric on purpose: an onset snaps up at 0.70 so a drum hit is
                // never slow-filtered, while the way down keeps the 0.30 average that
                // kills the jitter. The up side is where perceived latency hides --
                // a kick band used to crawl through 0.30 both ways for ~57ms before
                // the renderer even saw it.
                for (int b = 0; b < BANDS; b++)
                {
                    if (bandCoverage[b] >= 4.0 || !bandAvgPrimed) bandAvg[b] = bandDb[b];
                    else
                    {
                        double av = bandDb[b] > bandAvg[b] ? 0.70 : 0.30;
                        bandAvg[b] += (bandDb[b] - bandAvg[b]) * av;
                    }
                    bandDb[b] = bandAvg[b];
                }
                bandAvgPrimed = true;
            }
            else
            {
                for (int b = 0; b < BANDS; b++) bandDb[b] = -200.0;
            }

            // Reference level from a high percentile rather than the peak.
            // A single whistle or a cymbal crash sets the absolute maximum far
            // above where the rest of the music sits, so anchoring to it pins
            // every ordinary band to the floor and the display looks dead.
            // 96th of 64 bands is two or three bands below the loudest: sharp
            // enough that the floor sits near the bottom of the scale, loose
            // enough that one hot band does not darken the whole display.
            double refDb = Percentile();

            // Spectral gate, relative to the loudest band of this frame.
            //
            // Without it the display has almost no contrast. Measured with a
            // synthetic comb (8 tones, known amplitudes) and single sines:
            // a lone 60 Hz tone lit the top of the scale at 147/255 -- 78% of
            // the level of the band actually carrying the tone -- because a
            // Hann window's first sidelobe sits only -31 dB down, the AGC
            // anchors on the 92nd percentile (i.e. on that sidelobe floor),
            // and the gamma curve then lifts it further. Eight tones came out
            // at 1.2-1.8x peak-to-valley, and the bar-height order did not
            // match the amplitude order at all.
            //
            // So: anything more than GATE_DB below the loudest band is pushed
            // down at GATE_SLOPE instead of being shown. A slope rather than a
            // cliff, because a hard cut makes bands flicker on and off as they
            // cross the threshold. Real music spans 25-40 dB between bands, so
            // this only touches the noise floor and whatever the window leaks.
            //
            // Runs after Percentile() on purpose: the AGC must keep tracking
            // the body of the music, not the gated result.
            double gate = frameMaxDb - GATE_DB;
            for (int b = 0; b < BANDS; b++)
                if (bandDb[b] < gate) bandDb[b] = gate - (gate - bandDb[b]) * GATE_SLOPE;

            // ---- Pass 2: automatic gain -------------------------------
            // A fixed dB window cannot serve every track: a quiet acoustic
            // song and a loud master land in completely different places, so
            // one of them always renders as a flat line or a clipped wall.
            bool silent = n != FFT_SIZE || frameRms < SILENT_RMS;
            if (silent)
            {
                // Let everything settle toward the floor rather than freezing.
                for (int b = 0; b < BANDS; b++) smooth[b] *= 0.72f;
                for (int b = 0; b < BANDS; b++) peak[b] = 0;
            }
            else
            {
                double want = refDb + DB_HEADROOM;
                if (want < AGC_MIN_DB) want = AGC_MIN_DB;
                if (want > agcCeil) agcCeil += (want - agcCeil) * AGC_ATTACK;
                else agcCeil += (want - agcCeil) * AGC_RELEASE;
                agcFloor = agcCeil - DB_RANGE;
            }

            // ---- Pass 3: map, smooth, hold peaks ----------------------
            var sb = new StringBuilder(BANDS * 4 + 32);
            sb.Append("{\"event\":\"spectrum\",\"v\":[");
            for (int b = 0; b < BANDS; b++)
            {
                double v = silent ? 0.0 : (bandDb[b] - agcFloor) / DB_RANGE;
                if (v < 0) v = 0;
                if (v > 1) v = 1;

                // Gamma below 1 lifts the middle of the range. 0.68 used to be
                // safe because the AGC pinned the loudest band; it also squashed
                // whatever contrast there was, which is what the gate above is
                // now fighting for. 0.80 keeps the lift without flattening it.
                v = Math.Pow(v, 0.80);

                // Attack fast, release fast. This filter used to be 0.22/0.78 on the way
                // down, a 3.5-frame tail that showed up as the bar visibly
                // trailing every drum hit. The renderer does the smoothing
                // that actually reads well, and it knows the real frame time;
                // keeping a second long tail here just stacked delay on delay.
                float t2 = (float)v;
                if (t2 > smooth[b]) smooth[b] = t2 * (float)SMOOTH_UP + smooth[b] * (1.0f - (float)SMOOTH_UP);
                else smooth[b] = t2 * (float)SMOOTH_DOWN + smooth[b] * (1.0f - (float)SMOOTH_DOWN);

                int target = (int)Math.Round(smooth[b] * 255.0);
                if (target > peak[b]) peak[b] = target;
                // Decay proportionally, not by a fixed step. The bar itself
                // decays multiplicatively, so a fixed-step peak falls far too
                // slowly and ends up hovering well above the bar tip as a
                // detached floating dash. Matching the shape keeps the cap
                // sitting just above the bar, where it belongs.
                else peak[b] *= 0.93f;

                if (b > 0) sb.Append(',');
                sb.Append(target);
            }
            sb.Append("],\"p\":[");
            for (int b = 0; b < BANDS; b++)
            {
                if (b > 0) sb.Append(',');
                sb.Append((int)Math.Round(peak[b] < 0 ? 0 : peak[b]));
            }
            sb.Append("]}");
            Send(sb.ToString());
        }
        catch { }
    }

    static void BuildBandMap(int sr)
    {
        // Map each real FFT bin to its exact logarithmic position instead of
        // forcing every display band to own two bins. The old forward-widening
        // pushed low-frequency edges rightward until 1 kHz landed ~14 bars too
        // far left. Adjacent interpolation preserves every bin without holes.
        double lo = Math.Log10(35.0), hi = Math.Log10(16000.0);
        double binHz = (double)sr / FFT_SIZE;
        Array.Clear(bandCoverage, 0, BANDS);
        for (int i = 0; i < binBand0.Length; i++)
        {
            binBand0[i] = -1;
            binBand1[i] = -1;
            binMix1[i] = 0;
        }
        for (int i = 1; i < FFT_SIZE / 2; i++)
        {
            double hz = i * binHz;
            if (hz > 16000.0) continue;
            double p = (Math.Log10(hz) - lo) / (hi - lo) * (BANDS - 1);
            if (p < 0) p = 0;
            if (p > BANDS - 1) p = BANDS - 1;
            int b0 = (int)Math.Floor(p);
            int b1 = Math.Min(BANDS - 1, b0 + 1);
            double mix = b1 == b0 ? 0.0 : p - b0;
            binBand0[i] = b0;
            binBand1[i] = b1;
            binMix1[i] = mix;
            bandCoverage[b0] += 1.0 - mix;
            if (b1 != b0) bandCoverage[b1] += mix;
        }
    }

    // Hann window: without it spectral leakage smears every band and the
    // whole display saturates.
    static double[] window = null;

    static void BuildWindow()
    {
        window = new double[FFT_SIZE];
        for (int i = 0; i < FFT_SIZE; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1));
    }

    static double[] dbScratch = new double[BANDS];
    static double[] bandPower = new double[BANDS];

    static double Percentile()
    {
        // Sort a COPY. bandDb still holds the original band order that pass 3
        // indexes by band number, and sorting it in place would scramble the
        // spectrum into a meaningless jumble.
        Array.Copy(bandDb, dbScratch, BANDS);
        Array.Sort(dbScratch);
        int idx = (int)(BANDS * 0.96);
        if (idx >= BANDS) idx = BANDS - 1;
        return dbScratch[idx];
    }

    static int frame = 0;
    static double frameRms = 0;
    // Age, in ms, of the newest audio sample in the winning tap's ring at the
    // moment the frame was computed. This is the real capture-to-emit latency
    // and it is measured, not derived: whatever the WASAPI buffer, the thread
    // wakeup and the ring all cost shows up here. The FFT window adds a further
    // FFT_SIZE/sampleRate/2 on top, which is analytic.
    static int frameAgeMs = 0;
    static int frameAgeMax = 0;
}
