using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PromptForge.Mac.Voice;

/// <summary>
/// Records the default microphone as 16 kHz mono 16-bit PCM (what Whisper uses internally) with an
/// AudioQueue. Audio is kept in memory only, never written to disk. macOS asks for microphone
/// permission the first time; without it the recording is silent and is never sent.
/// </summary>
internal sealed unsafe partial class VoiceRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferCount = 3;
    private const uint BufferBytes = SampleRate * 2 / 10; // 100 ms

    private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagSignedPacked = 0x4 | 0x8;

    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private GCHandle _self;
    private IntPtr _queue;
    private volatile bool _recording;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [LibraryImport(Lib)]
    private static partial int AudioQueueNewInput(AudioStreamBasicDescription* format, IntPtr callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [LibraryImport(Lib)]
    private static partial int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);

    [LibraryImport(Lib)]
    private static partial int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptionCount, IntPtr packetDescriptions);

    [LibraryImport(Lib)]
    private static partial int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [LibraryImport(Lib)]
    private static partial int AudioQueueStop(IntPtr queue, byte immediate);

    [LibraryImport(Lib)]
    private static partial int AudioQueueDispose(IntPtr queue, byte immediate);

    /// <summary>The raw 16-bit PCM from the last recording, for checking it contains sound before transcribing.</summary>
    public byte[] LastPcm { get; private set; } = [];

    public TimeSpan Duration
    {
        get
        {
            lock (_gate) return TimeSpan.FromSeconds(_pcm.Length / (double)(SampleRate * 2));
        }
    }

    public void Start()
    {
        var format = new AudioStreamBasicDescription
        {
            SampleRate = SampleRate,
            FormatId = FormatLinearPcm,
            FormatFlags = FlagSignedPacked,
            BytesPerPacket = 2,
            FramesPerPacket = 1,
            BytesPerFrame = 2,
            ChannelsPerFrame = Channels,
            BitsPerChannel = BitsPerSample,
        };
        _self = GCHandle.Alloc(this);
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, uint, IntPtr, void> callback = &OnBuffer;
        // A null run loop: callbacks arrive on the AudioQueue's own thread.
        Check(AudioQueueNewInput(&format, (IntPtr)callback, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out _queue), "open the microphone");
        for (var i = 0; i < BufferCount; i++)
        {
            Check(AudioQueueAllocateBuffer(_queue, BufferBytes, out var buffer), "allocate audio buffers");
            Check(AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero), "queue audio buffers");
        }
        _recording = true;
        Check(AudioQueueStart(_queue, IntPtr.Zero), "start recording");
    }

    /// <summary>Stops recording and returns the audio as a WAV file.</summary>
    public Task<byte[]> StopAsync()
    {
        if (!_recording || _queue == IntPtr.Zero) return Task.FromResult<byte[]>([]);
        _recording = false;
        AudioQueueStop(_queue, 1);
        byte[] pcm;
        lock (_gate) pcm = _pcm.ToArray();
        LastPcm = pcm;
        return Task.FromResult(ToWav(pcm));
    }

    [UnmanagedCallersOnly]
    private static void OnBuffer(IntPtr userData, IntPtr queue, IntPtr bufferRef, IntPtr startTime, uint packets, IntPtr descriptions)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not VoiceRecorder recorder) return;
            var buffer = (AudioQueueBuffer*)bufferRef;
            if (buffer->AudioDataByteSize > 0)
            {
                var data = new ReadOnlySpan<byte>((void*)buffer->AudioData, (int)buffer->AudioDataByteSize);
                lock (recorder._gate) recorder._pcm.Write(data);
            }
            if (recorder._recording) AudioQueueEnqueueBuffer(queue, bufferRef, 0, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into native code.
            Log.Error("audio callback failed", ex);
        }
    }

    internal static byte[] ToWav(byte[] pcm)
    {
        const int headerSize = 44;
        var wav = new byte[headerSize + pcm.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);                                  // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);                                   // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * Channels * BitsPerSample / 8); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], Channels * BitsPerSample / 8);        // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[headerSize..]);
        return wav;
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"Couldn't {what} (OSStatus {status}).");
    }

    public void Dispose()
    {
        _recording = false;
        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, 1);
            AudioQueueDispose(_queue, 1); // also frees the buffers; no callbacks after this returns
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        lock (_gate)
        {
            // Overwrite the recording before letting it go.
            if (_pcm.TryGetBuffer(out var segment)) segment.AsSpan().Clear();
            _pcm.Dispose();
        }
        LastPcm = [];
    }
}
