using System;
using System.IO;
using System.Threading;
using Dalamud.Plugin.Services;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HoliestFluffiness;

internal static class SoundEngine
{
    private static IPluginLog? log;

    // File-picker filter. The Label{.ext,.ext} collection syntax groups every format under one
    // "Sound files" entry instead of listing each extension separately. .ogg is decoded by the
    // bundled NVorbis; the rest go through Windows Media Foundation (.flac needs Win10+).
    internal const string FileFilter =
        "Sound files (.wav/mp3/ogg/flac){.wav,.mp3,.ogg,.flac},All files{.*}";

    internal static void Initialize(IPluginLog pluginLog) => log = pluginLog;

    internal static string Resolve(string configPath, string defaultRelative, string baseDir) =>
        string.IsNullOrEmpty(configPath) ? Path.Combine(baseDir, defaultRelative) : configPath;

    internal static void Play(string path, float volume)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        new Thread(() =>
        {
            try
            {
                // Media Foundation has no built-in Vorbis decoder, so route .ogg through NVorbis
                // and let everything else use the OS codecs (mp3/wav/wma/m4a/flac).
                using WaveStream reader = Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(path)
                    : new MediaFoundationReader(path);
                var sample = new VolumeSampleProvider(reader.ToSampleProvider())
                {
                    // Allow up to 1000% to boost quiet files; VolumeSampleProvider amplifies above 1.0.
                    Volume = Math.Clamp(volume, 0f, 10f),
                };
                using var output = new DirectSoundOut();
                output.Init(sample);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
            }
            catch (Exception ex) { log?.Warning(ex, $"[HF] SoundEngine: failed to play '{path}'"); }
        }) { IsBackground = true }.Start();
    }
}
