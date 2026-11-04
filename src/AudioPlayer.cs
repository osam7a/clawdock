using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Wrenlet
{
    // Plays the natural voice's sentences one after another through the plain waveOut API, and
    // knows how loud the moment playing right now is (for the notch's bars).
    class AudioPlayer
    {
        const int WindowMs = 40; // loudness is measured in steps this long

        class Clip { public byte[] Pcm; public double[] Loudness; public int Rate, Gen; }

        readonly Queue<Clip> queue = new Queue<Clip>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile int gen;      // Stop() starts a new generation; clips from an older one are dropped
        bool current;          // a clip is playing (guarded by queue)
        volatile float level;

        public bool Playing { get { lock (queue) return current || queue.Count > 0; } }
        public double Level { get { return Playing ? level : 0; } } // 0..1

        public AudioPlayer()
        {
            new Thread(Run) { IsBackground = true, Name = "Natural voice playback" }.Start();
        }

        // samples: mono, -1..1.
        public void Enqueue(float[] samples, int rate)
        {
            if (samples.Length == 0) return;
            var pcm = new byte[samples.Length * 2];
            int window = Math.Max(1, rate * WindowMs / 1000);
            var loud = new double[(samples.Length + window - 1) / window];
            for (int w = 0; w < loud.Length; w++)
            {
                double sum = 0;
                int end = Math.Min(samples.Length, (w + 1) * window);
                for (int i = w * window; i < end; i++)
                {
                    float v = Math.Max(-1f, Math.Min(1f, samples[i]));
                    short s = (short)(v * 32767);
                    pcm[2 * i] = (byte)s;
                    pcm[2 * i + 1] = (byte)(s >> 8);
                    sum += v * v;
                }
                loud[w] = Math.Min(1, Math.Sqrt(sum / Math.Max(1, end - w * window)) * 4);
            }
            lock (queue) queue.Enqueue(new Clip { Pcm = pcm, Loudness = loud, Rate = rate, Gen = gen });
            wake.Set();
        }

        // The generation to tag clips with: Enqueue only ones made after the last Stop().
        public int Generation { get { return gen; } }

        public void Enqueue(float[] samples, int rate, int generation)
        {
            if (generation == gen) Enqueue(samples, rate);
        }

        // Stops at once and drops whatever was waiting.
        public void Stop()
        {
            lock (queue) { gen++; queue.Clear(); }
            wake.Set();
        }

        void Run()
        {
            IntPtr wave = IntPtr.Zero;
            int openRate = 0;
            while (true)
            {
                Clip clip = null;
                lock (queue) { if (queue.Count > 0) { clip = queue.Dequeue(); current = true; } }
                if (clip == null) { wake.WaitOne(30000); continue; }
                try
                {
                    if (clip.Gen != gen) continue;
                    if (wave == IntPtr.Zero || openRate != clip.Rate)
                    {
                        if (wave != IntPtr.Zero) { waveOutClose(wave); wave = IntPtr.Zero; }
                        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)clip.Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)clip.Rate * 2 };
                        int r = waveOutOpen(out wave, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                        if (r != 0) { wave = IntPtr.Zero; throw new InvalidOperationException("Couldn't open the speakers (" + r + ")."); }
                        openRate = clip.Rate;
                    }
                    Play(wave, clip);
                }
                catch (Exception e) { Log.Write("Natural voice: " + e.Message); }
                finally { level = 0; lock (queue) current = false; }
            }
        }

        void Play(IntPtr wave, Clip clip)
        {
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR)), timeSize = Marshal.SizeOf(typeof(MMTIME));
            IntPtr data = Marshal.AllocHGlobal(clip.Pcm.Length), header = Marshal.AllocHGlobal(headerSize);
            try
            {
                Marshal.Copy(clip.Pcm, 0, data, clip.Pcm.Length);
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)clip.Pcm.Length }, header, false);
                // The device's position counts on from earlier clips: measure from where this one starts.
                var start = new MMTIME { wType = TIME_SAMPLES };
                waveOutGetPosition(wave, ref start, timeSize);
                waveOutPrepareHeader(wave, header, headerSize);
                waveOutWrite(wave, header, headerSize);
                int window = Math.Max(1, clip.Rate * WindowMs / 1000);
                bool reset = false;
                while (true)
                {
                    var h = (WAVEHDR)Marshal.PtrToStructure(header, typeof(WAVEHDR));
                    if ((h.dwFlags & WHDR_DONE) != 0) break;
                    if (clip.Gen != gen && !reset) { waveOutReset(wave); reset = true; continue; } // hands the buffer back as done
                    var t = new MMTIME { wType = TIME_SAMPLES };
                    if (waveOutGetPosition(wave, ref t, timeSize) == 0 && t.wType == TIME_SAMPLES)
                    {
                        long w = ((long)t.u - start.u) / window;
                        level = w >= 0 && w < clip.Loudness.Length ? (float)clip.Loudness[w] : 0;
                    }
                    Thread.Sleep(15);
                }
                waveOutUnprepareHeader(wave, header, headerSize);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(header);
            }
        }

        const uint WAVE_MAPPER = 0xFFFFFFFF, WHDR_DONE = 1, TIME_SAMPLES = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct MMTIME { public uint wType, u, pad; } // u: the sample count (TIME_SAMPLES); the union is 8 bytes

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MMTIME t, int size);
    }
}
