using System;
using System.IO;
using System.Threading;
using Dalamud.Plugin.Services;
using NAudio.Wave;

namespace HoliestFluffiness;

internal static class SoundEngine
{
    private static IPluginLog? log;

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
                using var reader  = new MediaFoundationReader(path);
                using var channel = new WaveChannel32(reader) { Volume = Math.Clamp(volume, 0f, 1f), PadWithZeroes = false };
                using var output  = new DirectSoundOut();
                output.Init(channel);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
            }
            catch (Exception ex) { log?.Warning(ex, $"[HF] SoundEngine: failed to play '{path}'"); }
        }) { IsBackground = true }.Start();
    }
}
