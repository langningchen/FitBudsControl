using System.Text;
using FitBudsControl.Bluetooth;
using FitBudsControl.Models;
using Microsoft.UI.Dispatching;

namespace FitBudsControl.Services;

public sealed class EarbudsService : IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettingsStore _settingsStore;
    private readonly FitBudsClient _client = new();
    private readonly FitBudsProtocol _protocol;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private readonly object _trafficSync = new();
    private readonly Queue<ProtocolTrafficEntry> _trafficHistory = new();
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _hasFullSnapshot;
    private bool _disposing;

    public EarbudsService(DispatcherQueue dispatcher, AppSettingsStore settingsStore)
    {
        _dispatcher = dispatcher;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        Settings.ReconnectIntervalSeconds = Math.Clamp(Settings.ReconnectIntervalSeconds, 1, 3600);
        Settings.ConnectTimeoutMs = Math.Clamp(Settings.ConnectTimeoutMs, 500, 10000);
        _protocol = new FitBudsProtocol(_client);
        _client.PacketObserved += OnPacketObserved;
        _client.TrafficObserved += OnTrafficObserved;
        _client.Disconnected += OnDisconnected;

        // Start the reconnect schedule immediately, even if this process has never
        // successfully connected before. The first attempt runs after the configured interval.
        ScheduleReconnect();
    }

    public event EventHandler? StateChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler<EarbudsEventArgs>? EarbudsEventOccurred;
    public event Action<ProtocolTrafficEntry>? TrafficObserved;

    public AppSettings Settings { get; private set; }
    public EarbudsState State { get; private set; } = new();
    public bool IsConnected => _client.IsConnected;
    public bool HasFullSnapshot => _hasFullSnapshot;

    public void SaveSettings(AppSettings settings)
    {
        settings.ReconnectIntervalSeconds = Math.Clamp(settings.ReconnectIntervalSeconds, 1, 3600);
        settings.ConnectTimeoutMs = Math.Clamp(settings.ConnectTimeoutMs, 500, 10000);
        Settings = settings;
        _settingsStore.Save(settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ProtocolTrafficEntry> GetTrafficHistory()
    {
        lock (_trafficSync)
        {
            return _trafficHistory.ToArray();
        }
    }

    public void ClearTrafficHistory()
    {
        lock (_trafficSync)
        {
            _trafficHistory.Clear();
        }
    }

    public void RescheduleReconnect()
        => RefreshReconnectSchedule();

    /// <summary>
    /// Establishes RFCOMM and performs one small initial snapshot. Repeated calls while
    /// already connected do not re-query the device; subsequent state changes are fed
    /// by PacketObserved whenever the earbuds send reports on their own.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
            {
                return;
            }

            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            // Connection establishment should not be held up by three full retry cycles.
            // One lightweight snapshot is enough here; passive reports and manual refresh
            // can fill any state that did not answer immediately.
            await RefreshQuickCoreAsync(cancellationToken, attempts: 1).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
            ResetDisconnectedState("未连接", string.Empty);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Refreshes the compact control-panel state shown to the user.</summary>
    public async Task RefreshQuickAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            await RefreshQuickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            var refreshed = await _protocol.QueryConfirmedStatusAsync(cancellationToken).ConfigureAwait(false);
            refreshed.IsConnected = true;
            refreshed.ConnectionDetail = "已连接";
            State = refreshed;
            ApplyTimer(refreshed.TimerMinutes);
            _hasFullSnapshot = true;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Loads the expensive prompt/timer/touch snapshot only once for the current RFCOMM
    /// connection. Reopening Settings does not repeatedly scan the device.
    /// </summary>
    public Task EnsureFullSnapshotAsync(CancellationToken cancellationToken = default)
        => _hasFullSnapshot && _client.IsConnected
            ? Task.CompletedTask
            : RefreshAllAsync(cancellationToken);

    public async Task RefreshDevicePageAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                return;
            }

            var battery = await _protocol.QueryBatteryAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var firmware = await _protocol.QueryFirmwareVersionAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var name = await _protocol.QueryNameAsync(cancellationToken).ConfigureAwait(false);

            ApplyBattery(battery);
            if (!string.IsNullOrWhiteSpace(firmware))
            {
                State.FirmwareVersion = firmware;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                State.DeviceName = name;
            }
            State.LastRefresh = DateTimeOffset.Now;
            State.LastError = string.Empty;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RefreshSoundPageAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                return;
            }

            var noise = await _protocol.QueryNoiseAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var scene = await _protocol.QuerySceneAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var soundQuality = await _protocol.QuerySoundQualityAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var equalizer = await _protocol.QueryEqualizerAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var prompt = await _protocol.QueryPromptAsync(cancellationToken).ConfigureAwait(false);
            var promptVolume = await _protocol.QueryLegacyPromptVolumeAsync(cancellationToken, attempts: 2).ConfigureAwait(false);
            var timer = await _protocol.QueryTimerAsync(cancellationToken).ConfigureAwait(false);
            var touch = await _protocol.QueryAllTouchAsync(cancellationToken).ConfigureAwait(false);

            State.NoiseMode = noise.Mode;
            State.AmbientDb = noise.AmbientDb;
            State.SceneMode = scene.Mode;
            State.SoundQualityMode = soundQuality.Mode;
            if (equalizer.Count == FitBudsProtocol.EqualizerBandCount)
            {
                State.EqualizerBands = equalizer;
            }
            State.PromptEnabled = prompt.Enabled;
            State.PromptVolume = promptVolume ?? prompt.Volume;
            ApplyTimer(timer);
            State.TouchAssignments = touch;
            UpdateCycleState();
            State.LastRefresh = DateTimeOffset.Now;
            State.LastError = string.Empty;
            NotifyChanged();
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<OperationResult> SetNoiseAsync(NoiseMode mode, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetNoiseAsync(mode, ct).ConfigureAwait(false);
            if (result.Success)
            {
                var current = await _protocol.QueryNoiseAsync(ct).ConfigureAwait(false);
                State.NoiseMode = current.Mode;
                State.AmbientDb = current.AmbientDb;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetAmbientAsync(int db, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetAmbientAsync(db, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.NoiseMode = NoiseMode.Ambient;
                State.AmbientDb = db;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetSceneAsync(SceneMode scene, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetSceneAsync(scene, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.SceneMode = scene;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetSoundQualityAsync(
        SoundQualityMode mode,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetSoundQualityAsync(mode, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.SoundQualityMode = mode;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> RefreshEqualizerAsync(CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var bands = await _protocol.QueryEqualizerAsync(ct).ConfigureAwait(false);
            if (bands.Count != FitBudsProtocol.EqualizerBandCount)
            {
                return OperationResult.Fail("未读取到均衡器参数");
            }

            State.EqualizerBands = bands;
            return OperationResult.Ok("均衡器参数已读取");
        }, cancellationToken);

    public Task<OperationResult> SetEqualizerAsync(
        IReadOnlyCollection<EqualizerBand> bands,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var desired = bands.OrderBy(band => band.BandIndex).ToArray();
            if (desired.Length != FitBudsProtocol.EqualizerBandCount ||
                desired.Select(band => band.BandIndex).Distinct().Count() != FitBudsProtocol.EqualizerBandCount)
            {
                return OperationResult.Fail("均衡器参数不完整");
            }

            var current = State.EqualizerBands.ToDictionary(band => band.BandIndex);
            var changed = desired.Where(band =>
                !current.TryGetValue(band.BandIndex, out var oldBand) ||
                oldBand.FrequencyHz != band.FrequencyHz ||
                Math.Abs(oldBand.GainDb - band.GainDb) > 0.001 ||
                oldBand.Q != band.Q).ToArray();

            if (changed.Length == 0)
            {
                State.EqualizerBands = desired;
                return OperationResult.Ok("均衡器参数没有变化");
            }

            var result = await _protocol.SetEqualizerAsync(changed, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.EqualizerBands = desired;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetPromptEnabledAsync(enabled, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.PromptEnabled = enabled;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetPromptVolumeAsync(int volume, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetPromptVolumeAsync(volume, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.PromptVolume = volume;
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> PlayMediaAsync(CancellationToken cancellationToken = default)
        => RunOperationAsync(ct => _protocol.PlayMediaAsync(ct), cancellationToken);

    public Task<OperationResult> PauseMediaAsync(CancellationToken cancellationToken = default)
        => RunOperationAsync(ct => _protocol.PauseMediaAsync(ct), cancellationToken);

    public Task<OperationResult> SetTimerAsync(int minutes, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetTimerAsync(minutes, ct).ConfigureAwait(false);
            if (result.Success)
            {
                ApplyTimer(minutes);
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> CancelTimerAsync(CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.CancelTimerAsync(ct).ConfigureAwait(false);
            if (result.Success)
            {
                ApplyTimer(0);
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetTouchAsync(byte gestureId, TouchFunction function, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetTouchAsync(gestureId, function, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.TouchAssignments = await _protocol.QueryAllTouchAsync(ct).ConfigureAwait(false);
                UpdateCycleState();
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetCycleMaskAsync(IReadOnlyCollection<NoiseMode> modes, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetCycleMaskAsync(modes, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.TouchAssignments = await _protocol.QueryAllTouchAsync(ct).ConfigureAwait(false);
                UpdateCycleState();
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> SetDeviceNameAsync(string name, CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            var result = await _protocol.SetDeviceNameAsync(name, ct).ConfigureAwait(false);
            if (result.Success)
            {
                State.DeviceName = name.Trim();
            }
            return result;
        }, cancellationToken);

    public Task<OperationResult> PowerOffDeviceAsync(CancellationToken cancellationToken = default)
        => RunDepartureOperationAsync(
            ct => _protocol.PowerOffDeviceAsync(ct),
            "设备关机指令已发送",
            cancellationToken);

    public Task<OperationResult> DisconnectDeviceCommandAsync(CancellationToken cancellationToken = default)
        => RunDepartureOperationAsync(
            ct => _protocol.DisconnectDeviceAsync(ct),
            "设备已按指令断开",
            cancellationToken);

    public Task<OperationResult> EnterPairingAsync(CancellationToken cancellationToken = default)
        => RunDepartureOperationAsync(
            ct => _protocol.EnterPairingAsync(ct),
            "设备已进入配对流程",
            cancellationToken);

    public Task<OperationResult> ResetDeviceAsync(CancellationToken cancellationToken = default)
        => RunDepartureOperationAsync(
            ct => _protocol.ResetDeviceAsync(ct),
            "设备重置指令已发送",
            cancellationToken);

    private async Task<OperationResult> RunDepartureOperationAsync(
        Func<CancellationToken, Task<OperationResult>> action,
        string disconnectedDetail,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            _ = CancelReconnectLoop();
            var result = await action(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return result;
            }

            // CE/CD/CF/07 can make the remote side disappear immediately. Close our
            // RFCOMM socket deliberately as well so the automatic reconnect loop does
            // not undo a user-requested disconnect/pair/reset/power operation.
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            await _client.DisconnectAsync().ConfigureAwait(false);
            _ = CancelReconnectLoop();
            ResetDisconnectedState(disconnectedDetail, string.Empty);
            return result;
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            return OperationResult.Fail(GetPublicErrorMessage(ex));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<OperationResult> SendDeveloperCommandAsync(
        ushort category,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(async ct =>
        {
            await _client.SendAsync(category, payload, ct).ConfigureAwait(false);
            return OperationResult.Ok("指令已发送");
        }, cancellationToken);

    private async Task<OperationResult> RunOperationAsync(
        Func<CancellationToken, Task<OperationResult>> action,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = await action(cancellationToken).ConfigureAwait(false);
            State.LastError = result.Success ? string.Empty : result.Detail;
            State.LastRefresh = DateTimeOffset.Now;
            NotifyChanged();
            return result;
        }
        catch (Exception ex)
        {
            UpdateError(ex);
            return OperationResult.Fail(GetPublicErrorMessage(ex));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        State.ConnectionDetail = "正在连接…";
        State.LastError = string.Empty;
        NotifyChanged();

        try
        {
            await _client.ConnectAsync(
                Settings.BluetoothMac,
                Settings.RfcommChannel,
                Settings.ConnectTimeoutMs,
                Settings.RequestTimeoutMs,
                cancellationToken).ConfigureAwait(false);
            _hasFullSnapshot = false;
            var wasConnected = State.IsConnected;
            State.IsConnected = true;
            State.ConnectionDetail = "已连接";
            State.DeviceMac = Settings.BluetoothMac;
            NotifyChanged();
            if (!wasConnected)
            {
                RaiseEarbudsEvent(EarbudsEventKind.Connected);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetDisconnectedState("未连接", string.Empty);
            throw;
        }
        catch (Exception ex)
        {
            var message = GetConnectionErrorMessage(ex);
            ResetDisconnectedState(message, message);
            if (ex is TimeoutException)
            {
                throw new TimeoutException(message, ex);
            }
            throw new IOException(message, ex);
        }
    }

    private static string GetConnectionErrorMessage(Exception ex)
        => ex is TimeoutException ? "连接超时" : "连接失败";

    private static string GetPublicErrorMessage(Exception ex)
        => ex is TimeoutException ? "操作超时" : ex is IOException ? "连接失败" : "操作失败";

    private async Task RefreshQuickCoreAsync(CancellationToken cancellationToken, int attempts = 3)
    {
        attempts = Math.Clamp(attempts, 1, 3);
        var noise = await _protocol.QueryNoiseAsync(cancellationToken, attempts).ConfigureAwait(false);
        var scene = await _protocol.QuerySceneAsync(cancellationToken, attempts).ConfigureAwait(false);
        var battery = await _protocol.QueryBatteryAsync(cancellationToken, attempts).ConfigureAwait(false);

        State.IsConnected = true;
        State.ConnectionDetail = "已连接";
        State.NoiseMode = noise.Mode;
        State.AmbientDb = noise.AmbientDb;
        State.SceneMode = scene.Mode;
        State.LastError = string.Empty;
        State.LastRefresh = DateTimeOffset.Now;

        ApplyBattery(battery);
        NotifyChanged();
    }

    private void OnTrafficObserved(ProtocolTrafficEntry entry)
    {
        lock (_trafficSync)
        {
            _trafficHistory.Enqueue(entry);
        }

        try
        {
            TrafficObserved?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TrafficObserved subscriber failed: {ex}");
        }
    }

    private void OnPacketObserved(Packet packet)
    {
        // RfcommSocketConnection raises this on its receive thread. Keep state mutation
        // on the WinUI dispatcher; request matching continues independently in the client.
        if (_dispatcher.HasThreadAccess)
        {
            ApplyObservedPacket(packet);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyObservedPacket(packet));
        }
    }

    private void ApplyObservedPacket(Packet packet)
    {
        if (!_client.IsConnected || !packet.ChecksumOk)
        {
            return;
        }

        var payload = packet.Payload.AsSpan();
        var recognized = true;
        EarbudsEventKind? observedEvent = null;
        var previousNoiseMode = State.NoiseMode;
        var previousSceneMode = State.SceneMode;
        var previousSoundQualityMode = State.SoundQualityMode;
        var previousLeftLow = IsBatteryLow(State.LeftBattery, Settings.LeftLowBatteryThreshold);
        var previousRightLow = IsBatteryLow(State.RightBattery, Settings.RightLowBatteryThreshold);
        var previousCaseLow = IsBatteryLow(State.CaseBattery, Settings.CaseLowBatteryThreshold);

        switch (packet.Category)
        {
            case 0xCC00:
            {
                var noise = FitBudsProtocol.DecodeNoise(payload);
                State.NoiseMode = noise.Mode;
                State.AmbientDb = noise.AmbientDb;
                if (previousNoiseMode is not null && State.NoiseMode is not null && previousNoiseMode != State.NoiseMode)
                {
                    observedEvent = EarbudsEventKind.NoiseModeChanged;
                }
                break;
            }
            case 0x0800:
                State.SceneMode = payload.Length > 0 && Enum.IsDefined(typeof(SceneMode), payload[0])
                    ? (SceneMode)payload[0]
                    : null;
                if (previousSceneMode is not null && State.SceneMode is not null && previousSceneMode != State.SceneMode)
                {
                    observedEvent = EarbudsEventKind.SoundModeChanged;
                }
                break;
            case FitBudsProtocol.SoundQualityReadCategory:
                if (payload.Length == 1 && Enum.IsDefined(typeof(SoundQualityMode), payload[0]))
                {
                    State.SoundQualityMode = (SoundQualityMode)payload[0];
                    if (previousSoundQualityMode is not null && previousSoundQualityMode != State.SoundQualityMode)
                    {
                        observedEvent = EarbudsEventKind.SoundModeChanged;
                    }
                }
                else
                {
                    recognized = false;
                }
                break;
            case FitBudsProtocol.EqualizerReadCategory:
            {
                var bands = FitBudsProtocol.DecodeEqualizer(payload);
                if (bands.Count == FitBudsProtocol.EqualizerBandCount)
                {
                    State.EqualizerBands = bands;
                }
                else
                {
                    recognized = false;
                }
                break;
            }
            case 0x8600:
                if (payload.Length >= 3 && payload[0] == 0x04)
                {
                    State.PromptEnabled = payload[1] switch { 0 => false, 1 => true, _ => null };
                    State.PromptVolume = payload[2];
                }
                else
                {
                    recognized = false;
                }
                break;
            case 0xD300:
                if (payload.Length == 2)
                {
                    ApplyTimer((payload[0] << 8) | payload[1]);
                }
                else
                {
                    recognized = false;
                }
                break;
            case 0xF200:
                ApplyBattery(FitBudsProtocol.DecodeBattery(payload));
                if ((!previousLeftLow && IsBatteryLow(State.LeftBattery, Settings.LeftLowBatteryThreshold)) ||
                    (!previousRightLow && IsBatteryLow(State.RightBattery, Settings.RightLowBatteryThreshold)) ||
                    (!previousCaseLow && IsBatteryLow(State.CaseBattery, Settings.CaseLowBatteryThreshold)))
                {
                    observedEvent = EarbudsEventKind.LowBattery;
                }
                break;
            case FitBudsProtocol.LegacyBatteryReadCategory:
                if (payload.Length == 1 && payload[0] <= 100)
                {
                    State.LegacyBatteryPercent = payload[0];
                }
                else
                {
                    recognized = false;
                }
                break;
            case FitBudsProtocol.FirmwareReadCategory:
                if (payload.Length == 3)
                {
                    State.FirmwareVersion = string.Join(".", payload.ToArray().Select(value => value.ToString("X")));
                }
                else
                {
                    recognized = false;
                }
                break;
            case FitBudsProtocol.LegacyPromptVolumeReadCategory:
                if (payload.Length == 1)
                {
                    State.PromptVolume = payload[0];
                }
                else
                {
                    recognized = false;
                }
                break;
            case 0xC800:
                if (payload.Length == 6)
                {
                    State.DeviceMac = string.Join(":", packet.Payload.Select(b => b.ToString("X2")));
                }
                else
                {
                    recognized = false;
                }
                break;
            case 0xC900:
                State.DeviceName = Encoding.UTF8.GetString(payload).Trim('\0', ' ');
                break;
            case 0xF000:
            case 0xF100:
                if (payload.Length >= 2)
                {
                    ApplyTouch(FitBudsProtocol.DecodeTouch(payload));
                }
                else
                {
                    recognized = false;
                }
                break;
            default:
                // C300/6800/D800 and other experimental categories are retained by
                // FitBudsClient.HistorySince, but they do not have confirmed UI semantics.
                recognized = false;
                break;
        }

        if (!recognized)
        {
            return;
        }

        State.IsConnected = true;
        State.ConnectionDetail = "已连接";
        State.LastRefresh = packet.Timestamp;
        State.LastError = string.Empty;
        NotifyChanged();
        if (observedEvent is { } kind)
        {
            RaiseEarbudsEvent(kind);
        }
    }

    private void ApplyTimer(int? minutes)
    {
        State.TimerMinutes = minutes;
        State.TimerDeadline = minutes is > 0
            ? DateTimeOffset.Now.AddMinutes(minutes.Value)
            : null;
    }

    private void ApplyBattery(BatterySnapshot? battery)
    {
        if (battery is null)
        {
            State.OnlineMask = 0;
            State.LeftBattery = null;
            State.RightBattery = null;
            State.CaseBattery = null;
            State.CaseState = null;
            State.IsCaseCharging = false;
            return;
        }

        State.OnlineMask = battery.OnlineMask;
        State.LeftBattery = battery.LeftPercent;
        State.RightBattery = battery.RightPercent;
        State.CaseBattery = battery.CasePercent;
        State.CaseState = battery.CaseState;
        State.IsCaseCharging = battery.IsCaseCharging;
    }

    private void ApplyTouch(TouchAssignment assignment)
    {
        var rows = State.TouchAssignments.ToDictionary(item => item.GestureId);
        if (assignment.Function == TouchFunction.NoiseMode && assignment.Extra is null &&
            rows.TryGetValue(assignment.GestureId, out var previous) && previous.Function == TouchFunction.NoiseMode)
        {
            assignment = assignment with { Extra = previous.Extra };
        }

        rows[assignment.GestureId] = assignment;
        State.TouchAssignments = FitBudsProtocol.TouchReadOrder
            .Where(rows.ContainsKey)
            .Select(id => rows[id])
            .ToArray();
        UpdateCycleState();
    }

    private void UpdateCycleState()
    {
        var cycle = GetCycleState(State.TouchAssignments);
        State.ObservedAncCycleMasks = cycle.Masks;
        State.SharedAncCycleMask = cycle.SharedMask;
    }

    private static (IReadOnlyList<byte> Masks, byte? SharedMask) GetCycleState(IReadOnlyList<TouchAssignment> rows)
    {
        var masks = rows
            .Where(r => r.Function == TouchFunction.NoiseMode && r.Extra is not null)
            .Select(r => r.Extra!.Value)
            .Distinct()
            .Order()
            .ToArray();
        return (masks, masks.Length == 1 ? masks[0] : null);
    }

    private void OnDisconnected(Exception? exception)
    {
        var detail = "连接已断开";
        var error = string.Empty;

        if (_dispatcher.HasThreadAccess)
        {
            ResetDisconnectedState(detail, error);
            ScheduleReconnect();
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                ResetDisconnectedState(detail, error);
                ScheduleReconnect();
            });
        }
    }

    private void ResetDisconnectedState(string detail, string error)
    {
        var wasConnected = State.IsConnected;
        _hasFullSnapshot = false;
        State.IsConnected = false;
        State.ConnectionDetail = detail;
        State.LeftBattery = null;
        State.RightBattery = null;
        State.CaseBattery = null;
        State.LegacyBatteryPercent = null;
        State.CaseState = null;
        State.IsCaseCharging = false;
        State.OnlineMask = 0;
        State.NoiseMode = null;
        State.AmbientDb = null;
        State.SceneMode = null;
        State.SoundQualityMode = null;
        State.EqualizerBands = Array.Empty<EqualizerBand>();
        State.PromptEnabled = null;
        State.PromptVolume = null;
        State.FirmwareVersion = string.Empty;
        State.TimerMinutes = null;
        State.TimerDeadline = null;
        State.TouchAssignments = Array.Empty<TouchAssignment>();
        State.SharedAncCycleMask = null;
        State.ObservedAncCycleMasks = Array.Empty<byte>();
        State.LastError = error;
        NotifyChanged();
        if (wasConnected)
        {
            RaiseEarbudsEvent(EarbudsEventKind.Disconnected);
        }
    }

    private static bool IsBatteryLow(int? percent, int threshold)
        => percent is int value && value <= threshold;

    private void RaiseEarbudsEvent(EarbudsEventKind kind)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => RaiseEarbudsEvent(kind));
            return;
        }

        try
        {
            EarbudsEventOccurred?.Invoke(this, new EarbudsEventArgs(kind));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EarbudsEventOccurred subscriber failed: {ex}");
        }
    }

    private void RefreshReconnectSchedule()
    {
        if (_disposing || _client.IsConnected)
        {
            CancelReconnectLoop();
            return;
        }

        ScheduleReconnect(restart: true);
    }

    private void ScheduleReconnect(bool restart = false)
    {
        CancellationTokenSource? previous = null;
        lock (_reconnectSync)
        {
            if (_disposing || _client.IsConnected)
            {
                return;
            }

            if (!restart && _reconnectTask is { IsCompleted: false })
            {
                return;
            }

            previous = _reconnectCts;
            var owner = new CancellationTokenSource();
            _reconnectCts = owner;
            _reconnectTask = ReconnectLoopAsync(owner);
        }

        previous?.Cancel();
    }

    private async Task ReconnectLoopAsync(CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            while (!token.IsCancellationRequested && !_disposing)
            {
                var seconds = Math.Clamp(Settings.ReconnectIntervalSeconds, 1, 3600);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);

                if (_disposing || _client.IsConnected)
                {
                    return;
                }

                try
                {
                    await ConnectAsync(token).ConfigureAwait(false);
                    if (_client.IsConnected)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // ConnectCoreAsync already publishes the failed connection state.
                    // Wait for the configured interval and try again.
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal cancellation when the interval changes or the app exits.
        }
        finally
        {
            lock (_reconnectSync)
            {
                if (ReferenceEquals(_reconnectCts, owner))
                {
                    _reconnectCts = null;
                    _reconnectTask = null;
                }
            }
            owner.Dispose();
        }
    }

    private Task? CancelReconnectLoop()
    {
        CancellationTokenSource? owner;
        Task? task;
        lock (_reconnectSync)
        {
            owner = _reconnectCts;
            task = _reconnectTask;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        owner?.Cancel();
        return task;
    }

    private void UpdateError(Exception ex)
    {
        State.LastError = GetPublicErrorMessage(ex);
        State.IsConnected = _client.IsConnected;
        if (!State.IsConnected)
        {
            ResetDisconnectedState("未连接", GetPublicErrorMessage(ex));
            return;
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (_dispatcher.HasThreadAccess)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcher.TryEnqueue(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        var reconnectTask = CancelReconnectLoop();
        if (reconnectTask is not null)
        {
            try
            {
                await reconnectTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _client.PacketObserved -= OnPacketObserved;
        _client.TrafficObserved -= OnTrafficObserved;
        _client.Disconnected -= OnDisconnected;
        await _client.DisposeAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }
}
