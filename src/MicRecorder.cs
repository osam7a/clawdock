using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeNotch
{
    // Records the default microphone as 16 kHz mono 16-bit PCM (what Whisper wants), through the
    // plain waveIn API. Every 50 ms it reports how loud it was and whether that sounded like
    // speech (louder than the room's background noise, which it keeps learning).
    class MicRecorder
    {
        public const int SampleRate = 16000;
        const int BufferMs = 50, Buffers = 8;

        public event Action<double, bool> Level; // 0..1, voiced; on the recording thread

        readonly MemoryStream pcm = new MemoryStream();
        Thread thread;
        volatile bool running;
        string error;
        double noiseFloor = 200;
        int nextOnStop;

        public void Start()
        {
            pcm.SetLength(0);
            error = null;
            running = true;
            var started = new ManualResetEvent(false);
            thread = new Thread(() => Record(started)) { IsBackground = true, Name = "Microphone" };
            thread.Start();
            started.WaitOne(2000);
            if (error != null) throw new IOException(error);
        }

        // Stops and returns what was recorded, as a WAV file's bytes.
        public byte[] Stop()
        {
            running = false;
            if (thread != null) thread.Join(2000);
            thread = null;
            lock (pcm) return Wav(pcm.ToArray());
        }

        // The first microphone open in a process takes about half a second (Windows loads its audio
        // stack), which would cut off your first word; later ones are quick. Open and close once early.
        public static void WarmUp()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2 };
                    IntPtr h;
                    if (waveInOpen(out h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) == 0) waveInClose(h);
                }
                catch { }
            });
        }

        public static double Seconds(byte[] wav) { return Math.Max(0, wav.Length - 44) / (SampleRate * 2.0); }

        void Record(ManualResetEvent started)
        {
            IntPtr wave = IntPtr.Zero;
            var headers = new IntPtr[Buffers];
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            int bytes = SampleRate * 2 * BufferMs / 1000;
            try
            {
                var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2 };
                int r = waveInOpen(out wave, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (r != 0) { error = "Couldn't open the microphone (" + r + ")."; started.Set(); return; }
                for (int i = 0; i < Buffers; i++)
                {
                    var h = new WAVEHDR { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes };
                    headers[i] = Marshal.AllocHGlobal(headerSize);
                    Marshal.StructureToPtr(h, headers[i], false);
                    waveInPrepareHeader(wave, headers[i], headerSize);
                    waveInAddBuffer(wave, headers[i], headerSize);
                }
                waveInStart(wave);
                started.Set();

                int next = 0;
                var chunk = new byte[bytes];
                while (running)
                {
                    var h = (WAVEHDR)Marshal.PtrToStructure(headers[next], typeof(WAVEHDR));
                    if ((h.dwFlags & WHDR_DONE) == 0) { Thread.Sleep(10); continue; }
                    int n = (int)h.dwBytesRecorded;
                    Marshal.Copy(h.lpData, chunk, 0, n);
                    lock (pcm) pcm.Write(chunk, 0, n);
                    Measure(chunk, n);
                    waveInAddBuffer(wave, headers[next], headerSize); // back in the queue
                    next = (next + 1) % Buffers;
                }
                nextOnStop = next;
            }
            catch (Exception e) { error = e.Message; started.Set(); }
            finally
            {
                if (wave != IntPtr.Zero)
                {
                    waveInReset(wave); // hands back the buffers, the one being filled included
                    for (int i = 0; i < Buffers; i++)
                    {
                        var p = headers[(nextOnStop + i) % Buffers];
                        if (p == IntPtr.Zero) continue;
                        var h = (WAVEHDR)Marshal.PtrToStructure(p, typeof(WAVEHDR));
                        if ((h.dwFlags & WHDR_DONE) == 0 || h.dwBytesRecorded == 0) continue;
                        var tail = new byte[h.dwBytesRecorded];
                        Marshal.Copy(h.lpData, tail, 0, tail.Length);
                        lock (pcm) pcm.Write(tail, 0, tail.Length);
                    }
                    foreach (var p in headers)
                    {
                        if (p == IntPtr.Zero) continue;
                        waveInUnprepareHeader(wave, p, headerSize);
                        var h = (WAVEHDR)Marshal.PtrToStructure(p, typeof(WAVEHDR));
                        Marshal.FreeHGlobal(h.lpData);
                        Marshal.FreeHGlobal(p);
                    }
                    waveInClose(wave);
                }
            }
        }

        void Measure(byte[] chunk, int n)
        {
            double sum = 0;
            int count = n / 2;
            for (int i = 0; i + 1 < n; i += 2) { double v = (short)(chunk[i] | (chunk[i + 1] << 8)); sum += v * v; }
            double rms = count > 0 ? Math.Sqrt(sum / count) : 0;
            bool voiced = rms > Math.Max(noiseFloor * 2.5, 350);
            // The floor follows quiet stretches quickly and loud ones slowly.
            noiseFloor = rms < noiseFloor ? noiseFloor * 0.8 + rms * 0.2 : noiseFloor * 0.995 + rms * 0.005;
            if (Level != null) Level(Math.Min(1, rms / 6000), voiced);
        }

        static byte[] Wav(byte[] data)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + data.Length);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(data.Length);
            w.Write(data);
            w.Flush();
            return ms.ToArray();
        }

        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const uint WHDR_DONE = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    }
}
