using NAudio.Wave;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SoundBoard.Core.Audio;

/// <summary>
/// Windows output sink. Tries WASAPI (per-device, by MMDevice ID) first, then
/// falls back to WaveOut. NAudio.Wasapi / NAudio.WinMM are loaded by reflection
/// because they are only restored on Windows by the .csproj conditions —
/// referencing them statically would break the cross-platform build.
/// </summary>
public sealed class NAudioWindowsBackend : IAudioOutputBackend
{
    private IWavePlayer? _wavePlayer;
    private ISampleProvider? _source;
    private string? _preferredDeviceId;
    private bool _disposed;

    public bool IsPlaying => _wavePlayer?.PlaybackState == PlaybackState.Playing;

    public void Init(ISampleProvider source, string? preferredDeviceId)
    {
        _source = source;
        _preferredDeviceId = preferredDeviceId;

        bool wasPlaying = IsPlaying;
        StopInternal();

        try
        {
            _wavePlayer = CreateWindowsPlayer();
            if (_wavePlayer == null) return;

            try
            {
                _wavePlayer.Init(source);
                if (wasPlaying) _wavePlayer.Play();
                Log.Info("Audio", $"Initialized {_wavePlayer.GetType().FullName}");
            }
            catch (Exception ex)
            {
                Log.Warn("Audio", $"Init failed for {_wavePlayer.GetType().Name}", ex);
                if (!_wavePlayer.GetType().Name.Contains("WaveOut"))
                {
                    Log.Info("Audio", "Falling back to default WaveOut");
                    _wavePlayer.Dispose();
                    _wavePlayer = CreateWaveOutPlayer(-1);
                    if (_wavePlayer != null)
                    {
                        _wavePlayer.Init(source);
                        if (wasPlaying) _wavePlayer.Play();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio", "Critical initialization error", ex);
        }
    }

    public void Play()
    {
        if (_wavePlayer == null && _source != null) Init(_source, _preferredDeviceId);
        _wavePlayer?.Play();
    }

    public void Pause() => _wavePlayer?.Pause();

    public void Stop() => StopInternal();

    private void StopInternal()
    {
        try { _wavePlayer?.Stop(); _wavePlayer?.Dispose(); } catch { }
        _wavePlayer = null;
    }

    private IWavePlayer? CreateWindowsPlayer()
    {
        Log.Debug("Audio", $"Creating Windows player for ID: {_preferredDeviceId ?? "DEFAULT"}");

        // WASAPI path: device IDs from MMDeviceEnumerator look like "{0.0.0.00000000}.{guid}".
        if (!string.IsNullOrEmpty(_preferredDeviceId) && _preferredDeviceId.Contains('{'))
        {
            try
            {
                var wasapiAssembly = Assembly.Load("NAudio.Wasapi");
                try { Assembly.Load("NAudio.Core"); } catch { }

                var wasapiType = wasapiAssembly.GetType("NAudio.Wave.WasapiOut");
                var enumType = wasapiAssembly.GetType("NAudio.CoreAudioApi.MMDeviceEnumerator");

                if (wasapiType != null && enumType != null)
                {
                    Type? shareModeType = wasapiAssembly.GetType("NAudio.CoreAudioApi.AudioShareMode")
                                       ?? wasapiAssembly.GetType("NAudio.CoreAudioApi.AudioClientShareMode");

                    if (shareModeType == null)
                    {
                        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            shareModeType = assembly.GetType("NAudio.CoreAudioApi.AudioShareMode")
                                         ?? assembly.GetType("NAudio.CoreAudioApi.AudioClientShareMode")
                                         ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "AudioShareMode" || t.Name == "AudioClientShareMode");
                            if (shareModeType != null) break;
                        }
                    }

                    if (shareModeType != null)
                    {
                        var enumerator = Activator.CreateInstance(enumType);
                        var getMethod = enumType.GetMethod("GetDevice");
                        var device = getMethod?.Invoke(enumerator, new object[] { _preferredDeviceId! });
                        if (device != null)
                        {
                            var shared = Enum.ToObject(shareModeType, 0); // 0 = Shared
                            var player = (IWavePlayer)Activator.CreateInstance(wasapiType, new object[] { device, shared, true, 100 })!;
                            Log.Info("Audio", $"Created WASAPI player for {_preferredDeviceId}");
                            return player;
                        }
                    }
                    else
                    {
                        Log.Warn("Audio", "Couldn't resolve AudioShareMode/AudioClientShareMode type");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Audio", "WASAPI creation exception", ex); }
        }

        // WaveOut fallback. Try to map a WASAPI device name to a WaveOut index.
        int deviceNumber = -1;
        if (!string.IsNullOrEmpty(_preferredDeviceId))
        {
            if (int.TryParse(_preferredDeviceId, out int id))
            {
                deviceNumber = id;
            }
            else
            {
                try
                {
                    var devices = GetDevices().ToList();
                    var selectedDevice = devices.FirstOrDefault(d => d.Id == _preferredDeviceId);
                    if (selectedDevice != null)
                    {
                        var winMM = Assembly.Load("NAudio.WinMM");
                        var waveOutType = winMM.GetType("NAudio.Wave.WaveOut");
                        if (waveOutType != null)
                        {
                            var deviceCount = (int)waveOutType.GetProperty("DeviceCount")!.GetValue(null)!;
                            var getCapsMethod = waveOutType.GetMethod("GetCapabilities");

                            for (int i = 0; i < deviceCount; i++)
                            {
                                var caps = getCapsMethod?.Invoke(null, new object[] { i });
                                var productName = caps?.GetType().GetProperty("ProductName")?.GetValue(caps) as string;

                                // WaveOut names are truncated to 31 chars
                                if (productName != null &&
                                    (selectedDevice.Name.Contains(productName) ||
                                     productName.Contains(selectedDevice.Name.Substring(0, Math.Min(31, selectedDevice.Name.Length)))))
                                {
                                    deviceNumber = i;
                                    Log.Debug("Audio", $"Mapped WASAPI ID to WaveOut index: {i} ({productName})");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        return CreateWaveOutPlayer(deviceNumber);
    }

    private static IWavePlayer? CreateWaveOutPlayer(int deviceNumber)
    {
        Log.Debug("Audio", $"Initializing WaveOut (index: {deviceNumber})");
        try
        {
            var winMM = Assembly.Load("NAudio.WinMM");
            var type = winMM.GetType("NAudio.Wave.WaveOutEvent");
            if (type != null)
            {
                var player = (IWavePlayer)Activator.CreateInstance(type)!;
                type.GetProperty("DeviceNumber")?.SetValue(player, deviceNumber);
                return player;
            }
        }
        catch (Exception ex) { Log.Error("Audio", "WaveOut creation error", ex); }
        return null;
    }

    public IEnumerable<AudioDevice> GetDevices()
    {
        var devices = new List<AudioDevice>();
        try
        {
            var assembly = Assembly.Load("NAudio.Wasapi");
            var enumType = assembly.GetType("NAudio.CoreAudioApi.MMDeviceEnumerator");
            if (enumType != null)
            {
                var enumerator = Activator.CreateInstance(enumType);
                var endpoints = (System.Collections.IEnumerable)enumType.GetMethod("EnumerateAudioEndPoints")!
                    .Invoke(enumerator, new object[] { 0, 1 })!;
                foreach (var endpoint in endpoints)
                {
                    string id = endpoint.GetType().GetProperty("ID")?.GetValue(endpoint) as string ?? "";
                    string name = endpoint.GetType().GetProperty("FriendlyName")?.GetValue(endpoint) as string ?? "";
                    if (!string.IsNullOrEmpty(name)) devices.Add(new AudioDevice { Id = id, Name = name });
                }
            }
        }
        catch { }
        return devices;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopInternal();
        _disposed = true;
    }
}
