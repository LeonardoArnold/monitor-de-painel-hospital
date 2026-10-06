using System.Runtime.InteropServices;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Logging;

namespace GHospPanelWatchdog.Audio;

internal sealed record AudioDeviceInfo(string Id, string FriendlyName, uint State, bool IsDefault)
{
    public bool IsActive => (State & DeviceState.Active) != 0;
    public override string ToString() => FriendlyName + (IsDefault ? "  (padrão atual)" : "") + (IsActive ? "" : "  (desconectado)");
}

internal sealed record AudioStatus(
    bool Configured,
    bool Present,
    string DeviceName,
    bool IsDefault,
    bool? Muted,
    int? VolumePercent,
    string Message);

/// <summary>
/// Garante que o dispositivo de áudio do painel seja o padrão e esteja desmutado, usando apenas
/// APIs do Windows (Core Audio + IPolicyConfig). Nunca altera o nível de volume.
/// </summary>
internal sealed class AudioService : IDisposable
{
    /// <summary>Contexto das alterações feitas pelo watchdog (para ignorar o próprio eco).</summary>
    private static Guid _ourContext = new("6f0a3f8e-3b3a-4c38-9c41-7d1f1c2b8a01");

    private const int MaxCorrectionsPerMinute = 12;

    private readonly Action<string> _onChange;
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _notificationClient;
    private IAudioEndpointVolume? _endpointVolume;
    private VolumeCallback? _volumeCallback;
    private string? _volumeDeviceId;
    private readonly Queue<DateTime> _corrections = new();

    private bool _lastPresent = true;
    private bool _lastWasDefault = true;
    private bool _conflictLogged;

    public AudioService(Action<string> onChange) => _onChange = onChange;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _notificationClient = new NotificationClient(this);
            int hr = _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            if (hr != 0) Log.Warn($"Não foi possível registrar eventos de áudio (0x{hr:X8}); usando verificação periódica.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar o Core Audio", ex);
        }
    }

    // ------------------------------------------------------------------ Enumeração

    public static List<AudioDeviceInfo> ListOutputDevices(bool includeInactive = false)
    {
        var result = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string? defId = GetDefaultId(en, ERole.eMultimedia);
            uint mask = includeInactive ? (DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled) : DeviceState.Active;
            if (en.EnumAudioEndpoints(EDataFlow.eRender, mask, out col) != 0) return result;
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                try
                {
                    dev.GetId(out var id);
                    dev.GetState(out var state);
                    result.Add(new AudioDeviceInfo(id, GetFriendlyName(dev), state, id == defId));
                }
                finally { ComNative.Release(dev); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar dispositivos de áudio", ex);
        }
        finally
        {
            ComNative.Release(col);
            ComNative.Release(en);
        }
        return result.OrderBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? GetDefaultId(IMMDeviceEnumerator en, ERole role)
    {
        if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var dev) != 0 || dev == null) return null;
        try { return dev.GetId(out var id) == 0 ? id : null; }
        finally { ComNative.Release(dev); }
    }

    private static string GetFriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /*STGM_READ*/, out var store) != 0) return "(sem nome)";
        try
        {
            var key = PropKeys.DeviceFriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return "(sem nome)";
            try
            {
                const ushort VT_LPWSTR = 31;
                return pv.vt == VT_LPWSTR && pv.p1 != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p1) ?? "(sem nome)" : "(sem nome)";
            }
            finally { ComNative.PropVariantClear(ref pv); }
        }
        finally { ComNative.Release(store); }
    }

    // ------------------------------------------------------------------ Garantia

    /// <summary>
    /// Compara o estado real do áudio com o desejado e corrige somente o que divergir.
    /// <paramref name="panelProcessTree"/> é avaliado apenas se houver sessão mutada.
    /// </summary>
    public AudioStatus Ensure(AudioSelection sel, RecoveryOptions rec, Func<HashSet<int>?> panelProcessTree)
    {
        if (string.IsNullOrEmpty(sel.DeviceId))
            return new AudioStatus(false, false, "", false, null, null, "Não configurado");
        if (_enumerator == null)
            return new AudioStatus(true, false, sel.FriendlyName, false, null, null, "Core Audio indisponível");

        IMMDevice? dev = null;
        try
        {
            dev = FindTargetDevice(sel, out string deviceId);
            if (dev == null)
            {
                if (_lastPresent) Log.Info($"Áudio: dispositivo \"{sel.FriendlyName}\" ausente — aguardando reconexão (TV desligada/desconectada?)");
                _lastPresent = false;
                DropEndpointVolume();
                return new AudioStatus(true, false, sel.FriendlyName, false, null, null, "Dispositivo ausente — aguardando");
            }
            if (!_lastPresent) Log.Info($"Áudio: dispositivo \"{sel.FriendlyName}\" detectado");
            _lastPresent = true;

            // 1) Dispositivo padrão
            bool isDefault = true;
            if (rec.EnsureAudioDevice)
            {
                var roles = new List<ERole>();
                if (sel.SetForConsoleAndMultimedia) { roles.Add(ERole.eConsole); roles.Add(ERole.eMultimedia); }
                if (sel.SetForCommunications) roles.Add(ERole.eCommunications);
                var wrong = roles.Where(r => GetDefaultId(_enumerator, r) != deviceId).ToList();
                if (wrong.Count > 0)
                {
                    if (CanCorrect())
                    {
                        var current = GetDefaultName(ERole.eMultimedia);
                        SetDefault(deviceId, wrong);
                        isDefault = roles.All(r => GetDefaultId(_enumerator, r) == deviceId);
                        if (isDefault) Log.Info($"Áudio alterado para \"{sel.FriendlyName}\" (estava em \"{current}\")");
                        else Log.WarnThrottled($"Áudio: não foi possível definir \"{sel.FriendlyName}\" como padrão");
                    }
                    else isDefault = false;
                }
                else if (!_lastWasDefault) { /* voltou a ser padrão por conta própria */ }
                _lastWasDefault = isDefault;
            }

            // 2) Mute do endpoint (volume preservado)
            EnsureEndpointVolume(dev, deviceId);
            bool? muted = null;
            int? volume = null;
            if (_endpointVolume != null)
            {
                if (_endpointVolume.GetMute(out bool m) == 0) muted = m;
                if (_endpointVolume.GetMasterVolumeLevelScalar(out float v) == 0) volume = (int)Math.Round(v * 100);
                if (muted == true && rec.EnsureUnmuted && CanCorrect())
                {
                    Log.Info($"Mute detectado em \"{sel.FriendlyName}\" (volume {volume}%)");
                    int hr = _endpointVolume.SetMute(false, ref _ourContext);
                    if (hr == 0 && _endpointVolume.GetMute(out m) == 0 && !m)
                    {
                        muted = false;
                        Log.Info($"Mute removido — volume mantido em {volume}%");
                    }
                    else Log.WarnThrottled($"Áudio: falha ao remover mute (0x{hr:X8})");
                }
            }

            // 3) Sessão do Edge do painel mutada no mixer
            if (rec.UnmutePanelSession) UnmutePanelSessions(dev, panelProcessTree);

            string msg = !isDefault ? "Não é o padrão" : muted == true ? "Mutado" : "OK";
            return new AudioStatus(true, true, sel.FriendlyName, isDefault, muted, volume, msg);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao verificar áudio", ex);
            DropEndpointVolume();
            return new AudioStatus(true, false, sel.FriendlyName, false, null, null, "Erro (ver log)");
        }
        finally
        {
            ComNative.Release(dev);
        }
    }

    private IMMDevice? FindTargetDevice(AudioSelection sel, out string deviceId)
    {
        deviceId = sel.DeviceId;
        if (_enumerator!.GetDevice(sel.DeviceId, out var dev) == 0 && dev != null)
        {
            if (dev.GetState(out var st) == 0 && (st & DeviceState.Active) != 0) return dev;
            ComNative.Release(dev);
        }
        if (!sel.AllowNameFallback || string.IsNullOrWhiteSpace(sel.FriendlyName)) return null;

        // Fallback: o driver pode ter recriado o endpoint com outro ID; procura pelo mesmo nome.
        if (_enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var col) != 0) return null;
        try
        {
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var d) != 0) continue;
                if (GetFriendlyName(d) == sel.FriendlyName && d.GetId(out var id) == 0)
                {
                    Log.WarnThrottled($"Áudio: ID salvo não encontrado; usando dispositivo com o mesmo nome \"{sel.FriendlyName}\". Reselecione o áudio nas configurações para atualizar.");
                    deviceId = id;
                    return d;
                }
                ComNative.Release(d);
            }
        }
        finally { ComNative.Release(col); }
        return null;
    }

    private string GetDefaultName(ERole role)
    {
        if (_enumerator!.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var d) != 0 || d == null) return "nenhum";
        try { return GetFriendlyName(d); } finally { ComNative.Release(d); }
    }

    private static void SetDefault(string deviceId, IEnumerable<ERole> roles)
    {
        IPolicyConfig? pc = null;
        try
        {
            pc = (IPolicyConfig)new PolicyConfigComObject();
            foreach (var r in roles)
            {
                int hr = pc.SetDefaultEndpoint(deviceId, r);
                if (hr != 0) Log.WarnThrottled($"Áudio: SetDefaultEndpoint({r}) retornou 0x{hr:X8}");
            }
        }
        finally { ComNative.Release(pc); }
    }

    /// <summary>Limita correções para não "brigar" indefinidamente com outro programa.</summary>
    private bool CanCorrect()
    {
        var now = DateTime.UtcNow;
        while (_corrections.Count > 0 && now - _corrections.Peek() > TimeSpan.FromMinutes(1)) _corrections.Dequeue();
        if (_corrections.Count >= MaxCorrectionsPerMinute)
        {
            if (!_conflictLogged)
            {
                Log.Warn("Áudio: muitas correções em 1 minuto — outro programa pode estar alterando o áudio. Pausando correções por alguns segundos.");
                _conflictLogged = true;
            }
            return false;
        }
        _conflictLogged = false;
        _corrections.Enqueue(now);
        return true;
    }

    private void EnsureEndpointVolume(IMMDevice dev, string deviceId)
    {
        if (_endpointVolume != null && _volumeDeviceId == deviceId)
        {
            // Teste barato de validade (o objeto é invalidado se o dispositivo sumir).
            if (_endpointVolume.GetMute(out _) == 0) return;
            DropEndpointVolume();
        }
        DropEndpointVolume();
        var iid = ComNative.IID_IAudioEndpointVolume;
        if (dev.Activate(ref iid, ComNative.CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return;
        _endpointVolume = (IAudioEndpointVolume)obj;
        _volumeDeviceId = deviceId;
        _volumeCallback = new VolumeCallback(this);
        if (_endpointVolume.RegisterControlChangeNotify(_volumeCallback) != 0) _volumeCallback = null;
    }

    private void DropEndpointVolume()
    {
        if (_endpointVolume != null)
        {
            try { if (_volumeCallback != null) _endpointVolume.UnregisterControlChangeNotify(_volumeCallback); } catch { }
            ComNative.Release(_endpointVolume);
        }
        _endpointVolume = null;
        _volumeCallback = null;
        _volumeDeviceId = null;
    }

    private void UnmutePanelSessions(IMMDevice dev, Func<HashSet<int>?> panelProcessTree)
    {
        object? mgrObj = null;
        IAudioSessionEnumerator? en = null;
        try
        {
            var iid = ComNative.IID_IAudioSessionManager2;
            if (dev.Activate(ref iid, ComNative.CLSCTX_ALL, IntPtr.Zero, out mgrObj) != 0) return;
            var mgr = (IAudioSessionManager2)mgrObj;
            if (mgr.GetSessionEnumerator(out en) != 0) return;
            en.GetCount(out int count);
            HashSet<int>? tree = null;
            for (int i = 0; i < count; i++)
            {
                if (en.GetSession(i, out var s) != 0) continue;
                try
                {
                    if (s is not ISimpleAudioVolume vol || s is not IAudioSessionControl2 ctl) continue;
                    if (vol.GetMute(out bool muted) != 0 || !muted) continue;
                    tree ??= panelProcessTree() ?? new HashSet<int>();
                    if (ctl.GetProcessId(out uint pid) != 0 || !tree.Contains((int)pid)) continue;
                    if (vol.SetMute(false, ref _ourContext) == 0)
                        Log.Info("Sessão de áudio do Edge do painel estava mutada no mixer — desmutada (volume mantido)");
                }
                finally { ComNative.Release(s); }
            }
        }
        catch (Exception ex)
        {
            Log.WarnThrottled($"Áudio: falha ao verificar sessões do painel: {ex.Message}");
        }
        finally
        {
            ComNative.Release(en);
            ComNative.Release(mgrObj);
        }
    }

    public void Dispose()
    {
        DropEndpointVolume();
        if (_enumerator != null && _notificationClient != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
        }
        ComNative.Release(_enumerator);
        _enumerator = null;
    }

    // ------------------------------------------------------------------ Callbacks COM

    [ComVisible(true)]
    private sealed class NotificationClient(AudioService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, uint newState) => owner._onChange("audio-state");
        public void OnDeviceAdded(string deviceId) => owner._onChange("audio-added");
        public void OnDeviceRemoved(string deviceId) => owner._onChange("audio-removed");
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.eRender) owner._onChange("audio-default");
        }
        public void OnPropertyValueChanged(string deviceId, PROPERTYKEY key) { }
    }

    [ComVisible(true)]
    private sealed class VolumeCallback(AudioService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            try
            {
                // AUDIO_VOLUME_NOTIFICATION_DATA { GUID guidEventContext; BOOL bMuted; float fMasterVolume; ... }
                var ctx = Marshal.PtrToStructure<Guid>(data);
                bool muted = Marshal.ReadInt32(data, 16) != 0;
                if (ctx != _ourContext && muted) owner._onChange("audio-mute");
            }
            catch { }
            return 0;
        }
    }
}
