using System.Runtime.InteropServices;
using System.Text;

namespace OpenMagicka.Tests.Engine;

/// <summary>
/// Our FAudio (native/README.md) plays what stock FAudio does not, shown on a tiny XACT project made here (the game's
/// own is not in the repository): one interactive cue on a global variable, as Magicka's sprays are, whose only
/// variation plays a looping wave. It runs where the build ships our FAudio and its record (Windows); elsewhere the
/// FAudio found is stock or missing and it passes without asserting. SDL's dummy audio device stands in where there is
/// no sound card (CI).
/// </summary>
public class OwnFAudioTests
{
    /// <summary>The build record shipped next to our FAudio (native/win-x64/FAudio.SOURCE.txt, renamed).</summary>
    private const string Record = "FAudio.OpenMagicka.txt";

    private const ushort ContentVersion = 46;
    private const ushort Unnamed = 0xFFFF;
    private const byte NoInstanceLimit = 0xFF;
    private const byte ZeroDecibels = 180;                              // XACT's volume byte nearest 0 dB
    private const byte LoopForever = 255;
    private const int SampleRate = 22050;
    private const int Samples = SampleRate / 10;

    [Fact]
    public void AnInteractiveCue_HasASoundOncePlayed()
    {
        var ours = File.Exists(Path.Combine(AppContext.BaseDirectory, Record));
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            Assert.True(ours, $"CI on Windows must test our FAudio: {Record} is not beside the tests");
        }
        if (!ours)
        {
            return;                                                     // stock or no FAudio here
        }

        SDL3.SDL.SDL_SetHint(SDL3.SDL.SDL_HINT_AUDIO_DRIVER, "dummy");
        using var project = new Project(Settings(), SoundBank(), WaveBank());

        Assert.Equal(0u, FAudio.FACTSoundBank_Prepare(project.SoundBank, 0, 0, 0, out var cue));
        Assert.Equal(0u, FAudio.FACTCue_Play(cue));
        Assert.Equal(1, PlayingTracks(cue));                           // stock FAudio: 0, a cue playing silence

        FAudio.FACTCue_Stop(cue, FAudio.FACT_FLAG_STOP_IMMEDIATE);
        FAudio.FACTCue_Destroy(cue);
    }

    /// <summary>The tracks of the sound a cue plays (none when it has no sound).</summary>
    private static int PlayingTracks(IntPtr cue)
    {
        Assert.Equal(0u, FAudio.FACTCue_GetProperties(cue, out var properties));
        try
        {
            var sound = Marshal.OffsetOf<CueInstanceProperties>(nameof(CueInstanceProperties.SoundVariation))
                + Marshal.OffsetOf<SoundVariationProperties>(nameof(SoundVariationProperties.Sound)).ToInt32()
                + Marshal.OffsetOf<SoundProperties>(nameof(SoundProperties.NumTracks)).ToInt32();
            return Marshal.ReadInt16(properties, sound.ToInt32());
        }
        finally
        {
            SDL3.SDL.SDL_free(properties);                              // FAudio allocates with SDL
        }
    }

    // FACT.h's FACTCueInstanceProperties, whose friendly name is 255 bytes (FNA's binding has it as 2-byte chars).
    [StructLayout(LayoutKind.Sequential)]
    private struct CueInstanceProperties
    {
        public uint AllocAttributes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0xFF)]
        public byte[] FriendlyName;
        public int Interactive;
        public ushort IaVariableIndex;
        public ushort NumVariations;
        public byte MaxInstances;
        public byte CurrentInstances;
        public SoundVariationProperties SoundVariation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SoundVariationProperties
    {
        public FAudio.FACTVariationProperties Variation;
        public SoundProperties Sound;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SoundProperties
    {
        public ushort Category;
        public byte Priority;
        public short Pitch;
        public float Volume;
        public ushort NumTracks;
    }

    /// <summary>An engine with the project's settings, sound bank and wave bank, its buffers held until it is shut down.</summary>
    private sealed class Project : IDisposable
    {
        private readonly List<IntPtr> buffers = [];
        private readonly IntPtr engine;

        public IntPtr SoundBank { get; }

        public Project(byte[] settings, byte[] soundBank, byte[] waveBank)
        {
            Assert.Equal(0u, FAudio.FACTCreateEngine(0, out engine));
            var parameters = new FAudio.FACTRuntimeParameters
            {
                pGlobalSettingsBuffer = Hold(settings),
                globalSettingsBufferSize = (uint)settings.Length,
            };
            Assert.Equal(0u, FAudio.FACTAudioEngine_Initialize(engine, ref parameters));
            Assert.Equal(0u, FAudio.FACTAudioEngine_CreateInMemoryWaveBank(engine, Hold(waveBank), (uint)waveBank.Length, 0, 0, out _));
            Assert.Equal(0u, FAudio.FACTAudioEngine_CreateSoundBank(engine, Hold(soundBank), (uint)soundBank.Length, 0, 0, out var bank));
            SoundBank = bank;
        }

        private IntPtr Hold(byte[] data)
        {
            var buffer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);
            buffers.Add(buffer);
            return buffer;
        }

        public void Dispose()
        {
            FAudio.FACTAudioEngine_ShutDown(engine);
            FAudio.FACTAudioEngine_Release(engine);
            buffers.ForEach(Marshal.FreeHGlobal);
        }
    }

    /// <summary>A little-endian writer that can go back to fill in offsets.</summary>
    private sealed class Bytes
    {
        private readonly MemoryStream stream = new();
        private readonly BinaryWriter writer;

        public Bytes() => writer = new BinaryWriter(stream, Encoding.ASCII);

        public int Position => (int)stream.Position;

        public Bytes Ascii(string text) { writer.Write(Encoding.ASCII.GetBytes(text)); return this; }

        public Bytes Name(string text, int length) { writer.Write(Encoding.ASCII.GetBytes(text.PadRight(length, '\0'))); return this; }

        public Bytes Zero(int count) { writer.Write(new byte[count]); return this; }

        public Bytes U8(byte value) { writer.Write(value); return this; }

        public Bytes U16(ushort value) { writer.Write(value); return this; }

        public Bytes U32(uint value) { writer.Write(value); return this; }

        public Bytes F32(float value) { writer.Write(value); return this; }

        /// <summary>Room for an offset, filled in by <see cref="Here"/>.</summary>
        public Bytes Slot(out int slot) { slot = Position; return U32(0); }

        /// <summary>Points the slot at the current position.</summary>
        public int Here(int slot)
        {
            var here = Position;
            stream.Position = slot;
            writer.Write((uint)here);
            stream.Position = here;
            return here;
        }

        public byte[] ToArray() => stream.ToArray();
    }

    /// <summary>The global settings (.xgs): a category and one global, public variable from 0 to 1, starting at 0.</summary>
    private static byte[] Settings()
    {
        var b = new Bytes().Ascii("XGSF").U16(ContentVersion).U16(42).U16(0).Zero(8).U8(3);
        b.U16(1).U16(1).U16(0).U16(0).U16(0).U16(0).U16(0);           // categories, variables, blobs, RPCs, DSP presets and parameters
        b.Slot(out var categories).Slot(out var variables).U32(0).Slot(out var categoryNameIndex).U32(0)
            .Slot(out var variableNameIndex).Slot(out var categoryNames).Slot(out var variableNames).U32(0).U32(0).U32(0);

        b.Here(categories);
        b.U8(NoInstanceLimit).U16(0).U16(0).U8(0).U16(Unnamed).U8(ZeroDecibels).U8(1);
        b.Here(variables);
        b.U8(1).F32(0).F32(0).F32(1);                                   // public, global
        b.Here(categoryNameIndex);
        b.Slot(out var categoryName).U16(Unnamed);
        b.Here(variableNameIndex);
        b.Slot(out var variableName).U16(Unnamed);
        b.Here(categoryNames);
        b.Here(categoryName);
        b.Ascii("Global\0");
        b.Here(variableNames);
        b.Here(variableName);
        b.Ascii("Interactive\0");
        return b.ToArray();
    }

    /// <summary>The sound bank (.xsb): a complex cue whose interactive variation plays, for the variable at 0 to 1, a sound looping wave 0.</summary>
    private static byte[] SoundBank()
    {
        var b = new Bytes().Ascii("SDBK").U16(ContentVersion).U16(43).U16(0).Zero(8).U8(1);
        b.U16(0).U16(1).U16(0).U16(1).U8(1).U16(1).U16(0).U16(0);     // simple and complex cues, alignment, wave banks, sounds
        b.U32(0).Slot(out var complexCues).U32(uint.MaxValue).U32(0)  // no simple cues, no cue names
            .Slot(out var variations).U32(0).Slot(out var waveBankNames).U32(0).U32(0).Slot(out var sounds);
        b.Name("Test", 64);

        b.Here(waveBankNames);
        b.Name("Test", 64);

        var sound = b.Here(sounds);
        b.U8(0x01).U16(0).U8(ZeroDecibels).U16(0).U8(0).U16(0);       // complex, category 0
        b.U8(1);                                                        // one track
        b.U8(ZeroDecibels).Slot(out var events).U16(0).U16(0);         // no filter
        b.Here(events);
        b.U8(1).U32(1).U16(0).U8(0xFF);                                 // one event, PlayWave at 0
        b.U8(0).U16(0).U8(0).U8(LoopForever).U16(0).U16(0);            // wave 0 of wave bank 0

        b.Here(complexCues);
        b.U8(0).Slot(out var cueVariation).U32(uint.MaxValue).U8(NoInstanceLimit).U16(0).U16(0).U8(0);

        b.Here(variations);
        b.Here(cueVariation);
        b.U32(1 | (3 << 19)).U16(0).U16(0);                             // one entry, interactive on variable 0
        b.U32((uint)sound).F32(0).F32(1).U32(0);
        return b.ToArray();
    }

    /// <summary>The wave bank (.xwb), in memory: one wave of 16-bit mono PCM, a tenth of a second of a quiet tone.</summary>
    private static byte[] WaveBank()
    {
        const int dataSize = 96, entrySize = 24, header = 52;
        var waves = new byte[Samples * 2];
        for (var i = 0; i < Samples; i++)
        {
            BitConverter.TryWriteBytes(waves.AsSpan(i * 2), (short)(1000 * Math.Sin(i * 2 * Math.PI * 440 / SampleRate)));
        }

        var b = new Bytes().Ascii("WBND").U32(ContentVersion).U32(44);
        b.U32(header).U32(dataSize);                                                    // bank data
        b.U32(header + dataSize).U32(entrySize);                                        // entry metadata
        b.U32(0).U32(0).U32(0).U32(0);                                                  // no seek tables, no names
        b.U32(header + dataSize + entrySize).U32((uint)waves.Length);                  // wave data

        b.U32(0).U32(1).Name("Test", 64).U32(entrySize).U32(0).U32(4).U32(0).Zero(8);  // in memory, one entry
        const uint format = 0 | (1 << 2) | ((uint)SampleRate << 5) | (2u << 23) | (1u << 31); // PCM, mono, 16-bit
        b.U32((uint)Samples << 4).U32(format).U32(0).U32((uint)waves.Length).U32(0).U32(0);
        var bytes = b.ToArray();
        return [.. bytes, .. waves];
    }
}
