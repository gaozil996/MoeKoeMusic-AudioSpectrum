using System;
using System.Collections.Generic;
using System.IO;
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
    // time, which is what AVG_LOW does below. Resolution is bought with a few
    // frames of history instead of a longer window, because frames of history
    // cost latency only in proportion and can be made band-dependent.
    const int FFT_SIZE = 2048;
    const int BANDS = 64;
    const int MIN_BINS = 2;       // every band must span at least this many bins

    // Temporal smoothing per band, applied to the raw dB before anything else.
    // Only the first handful of bands are thin enough to need it; the ramp is
    // therefore steep (pow 0.35), so the low end gets stability and the rest
    // of the display is left as responsive as it was before.
    const double AVG_HIGH = 0.60; // strongest band: barely averaged
    const double AVG_LOW = 0.28;  // weakest band: tau ~ 2.6 frames

    // Release smoothing on the mapped 0..1 bar value. This is what the eye
    // reads as "the bars lag the music", so it is kept short. 0.55/0.45 is a
    // one-frame time constant; the visual smoothing lives in the renderer,
    // which has the real frame delta and can be tuned per display rate.
    const double SMOOTH_UP = 0.66;
    const double SMOOTH_DOWN = 0.45;
    const int MAX_MSG = 60 * 1024;
    const int FRAME_MS = 10;          // Windows rounds Thread.Sleep up to the
                                      // scheduler tick (~15.6 ms), so this
                                      // really runs at ~15.6 ms and cannot be
                                      // asked for less without spinning. Left
                                      // alone deliberately: burning a core to
                                      // recover 5 ms is a bad trade. 16 was the
                                      // trap -- it rounds to two ticks and
                                      // gives 33 fps.
    const double DB_RANGE = 40.0;     // visible dynamic range, in dB
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

        public readonly float[] Ring = new float[FFT_SIZE];
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
    static int[] edges = new int[BANDS + 1];
    static Cpx[] buf = new Cpx[FFT_SIZE];
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

    static void Main()
    {
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
                float sum = 0f;
                for (int c = 0; c < t.Ch; c++)
                {
                    sum += DecodeSample(raw, baseIdx + c * t.Bps, t.Bps, t.IsFloat);
                }
                float m = sum / t.Ch;
                double a = m < 0 ? -m : m;
                if (a > peak) peak = a;

                t.Ring[t.RingPos] = m;
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
        if (isFloat && bps == 4) return BitConverter.ToSingle(raw, off);
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
            float[] src = null;
            int pos = 0;
            lock (TapLock)
            {
                if (t != null && !t.Broken && t.RingLen >= FFT_SIZE)
                {
                    src = t.Ring; pos = t.RingPos; n = FFT_SIZE;
                    if (t.SampleRate != sampleRate) { sampleRate = t.SampleRate; BuildEdges(sampleRate); }
                }
            }

            double energy = 0;
            if (n == FFT_SIZE)
                for (int i = 0; i < FFT_SIZE; i++)
                {
                    double v = src[(pos + i) & (FFT_SIZE - 1)];
                    energy += v * v;
                }
            frameRms = n == FFT_SIZE ? Math.Sqrt(energy / FFT_SIZE) : 0;
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
                // Oldest sample first. RingPos points at the next slot to
                // write, so the oldest sample is the one it points at.
                for (int i = 0; i < FFT_SIZE; i++)
                {
                    float s = src[(pos + i) & (FFT_SIZE - 1)];
                    buf[i].X = s * window[i];
                    buf[i].Y = 0;
                }
                Fft(buf, FFT_SIZE);

                for (int b = 0; b < BANDS; b++)
                {
                    int s0 = edges[b], e0 = Math.Min(edges[b + 1], FFT_SIZE / 2);

                    // Sum ENERGY across the band, not the mean magnitude.
                    // Averaging over bins makes a wide high-frequency band
                    // read quieter than a narrow low-frequency one carrying
                    // the same energy -- which is why the old code needed a
                    // fake tilt.
                    double power = 0;
                    for (int i = s0; i < e0; i++)
                    {
                        double re = buf[i].X, im = buf[i].Y;
                        power += re * re + im * im;
                    }

                    // FFT_SIZE/4 is the amplitude one bin shows for a unit sine.
                    double amp = Math.Sqrt(power) / (FFT_SIZE * 0.25);
                    double db = 20.0 * Math.Log10(amp + 1e-9);
                    bandDb[b] = db;
                    if (db > frameMaxDb) frameMaxDb = db;
                }

                // Time-average, strongest response on the bands that span most
                // bins. First frame primes the average outright so a song
                // change does not fade in from a stale history.
                for (int b = 0; b < BANDS; b++)
                {
                    double a = AVG_LOW + (AVG_HIGH - AVG_LOW) * Math.Pow(b / (double)(BANDS - 1), 0.35);
                    if (!bandAvgPrimed) bandAvg[b] = bandDb[b];
                    else bandAvg[b] += (bandDb[b] - bandAvg[b]) * a;
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
            // The 92nd percentile tracks the body of the music.
            double refDb = Percentile();

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

                // A little gamma below 1 lifts the middle of the range. Safe to
                // do now that the gain control already pins the loudest band,
                // which is what used to drive every band to 255 and turn the
                // whole display into a solid wall.
                v = Math.Pow(v, 0.68);

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

    static void BuildEdges(int sr)
    {
        // 35 Hz - 16 kHz on a log scale.
        //
        // The band count is fixed at 64, so a too-short FFT leaves the
        // narrowest bands spanning a single bin (at 48 kHz / 2048 points one
        // bin is 23.4 Hz, so most bands held only 1-2 bins). A band that
        // thin carries almost no energy, which is why the left half of the
        // display stayed empty no matter how the gain was set.
        //
        // 4096 points at 48 kHz gives 11.7 Hz per bin, and MIN_BINS guarantees
        // no band is ever thinner than 3 bins -- 35 Hz across the narrow end,
        // which is wide enough to carry a stable magnitude. Going finer than
        // this only buys resolution the display does not use, at 85 ms of lag
        // per doubling.
        double lo = Math.Log10(35.0), hi = Math.Log10(16000.0);
        double binHz = (double)sr / FFT_SIZE;

        for (int i = 0; i <= BANDS; i++)
        {
            double hz = Math.Pow(10.0, lo + (hi - lo) * i / BANDS);
            int bin = (int)Math.Round(hz / binHz);
            edges[i] = Math.Max(1, Math.Min(FFT_SIZE / 2, bin));
        }

        // Widen any band that came out too thin, keeping them ordered and
        // non-overlapping. Bands can drift upward past the intended top, but
        // that only costs a little high-end resolution, which is better than
        // half the display reading zero.
        for (int i = 1; i <= BANDS; i++)
        {
            int want = edges[i - 1] + MIN_BINS;
            if (edges[i] < want) edges[i] = want;
        }
        // Clamp the top so the widening cannot run past Nyquist.
        for (int i = BANDS; i >= 1; i--)
        {
            int limit = i == BANDS ? FFT_SIZE / 2 : edges[i + 1];
            if (edges[i] > limit) edges[i] = limit;
        }
        for (int i = 1; i <= BANDS; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;
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

    static double Percentile()
    {
        // Sort a COPY. bandDb still holds the original band order that pass 3
        // indexes by band number, and sorting it in place would scramble the
        // spectrum into a meaningless jumble.
        Array.Copy(bandDb, dbScratch, BANDS);
        Array.Sort(dbScratch);
        int idx = (int)(BANDS * 0.92);
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
