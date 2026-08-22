using System.Text;
using FitBudsControl.Models;

namespace FitBudsControl.Bluetooth;

public sealed class FitBudsProtocol
{
    public const ushort SoundQualityReadCategory = 0xD500;
    public const ushort SoundQualityWriteCategory = 0xC400;
    public const ushort EqualizerReadCategory = 0x4300;
    public const ushort EqualizerWriteCategory = 0x4400;

    // Legacy mEDIFIER command IDs promoted to first-class compatibility interfaces.
    // The EC framing conversion is confirmed for the same command family; individual
    // device semantics should still be treated as compatibility behavior.
    public const ushort LegacyBatteryReadCategory = 0xD000;
    public const ushort FirmwareReadCategory = 0xC600;
    public const ushort LegacyPromptVolumeReadCategory = 0x0500;
    public const ushort PromptVolumeWriteCategory = 0x0600;
    public const ushort MediaControlCategory = 0xC200;
    public const ushort DevicePowerOffCategory = 0xCE00;
    public const ushort DeviceDisconnectCategory = 0xCD00;
    public const ushort DevicePairingCategory = 0xCF00;
    public const ushort DeviceResetCategory = 0x0700;
    public const ushort DeviceRenameCategory = 0xCA00;
    public const int DeviceNameMaxUtf8Bytes = 35;
    public const int EqualizerBandCount = 4;
    public const int EqualizerMinFrequencyHz = 20;
    public const int EqualizerMaxFrequencyHz = 8000;
    public const double EqualizerMinGainDb = -3.0;
    public const double EqualizerMaxGainDb = 3.0;
    public const decimal EqualizerMinQ = 0.7m;
    public const decimal EqualizerMaxQ = 2.0m;

    public static (int MinHz, int MaxHz) GetEqualizerFrequencyRange(byte bandIndex)
        => bandIndex switch
        {
            0 => (20, 200),
            1 => (201, 2000),
            2 => (2001, 4000),
            3 => (4001, 8000),
            _ => throw new ArgumentOutOfRangeException(nameof(bandIndex), "均衡器频段编号超出可用范围"),
        };

    public static bool IsEqualizerFrequencyValid(byte bandIndex, int frequencyHz)
    {
        if (bandIndex >= EqualizerBandCount)
        {
            return false;
        }

        var (minHz, maxHz) = GetEqualizerFrequencyRange(bandIndex);
        return frequencyHz >= minHz && frequencyHz <= maxHz;
    }

    public static readonly IReadOnlyDictionary<NoiseMode, string> NoiseLabels = new Dictionary<NoiseMode, string>
    {
        [NoiseMode.Deep] = "深度降噪",
        [NoiseMode.Comfort] = "舒适降噪",
        [NoiseMode.Wide] = "宽频降噪",
        [NoiseMode.Wind] = "防风降噪",
        [NoiseMode.Ambient] = "环境音",
        [NoiseMode.Off] = "关闭降噪",
    };

    public static readonly IReadOnlyDictionary<SceneMode, string> SceneLabels = new Dictionary<SceneMode, string>
    {
        [SceneMode.Music] = "音乐",
        [SceneMode.Game] = "游戏",
        [SceneMode.Spatial] = "空间音效",
    };

    public static readonly IReadOnlyDictionary<SoundQualityMode, string> SoundQualityLabels = new Dictionary<SoundQualityMode, string>
    {
        [SoundQualityMode.Classic] = "经典",
        [SoundQualityMode.BassBoost] = "低音增强",
        [SoundQualityMode.ClearVoice] = "清晰人声",
        [SoundQualityMode.TrebleBoost] = "高音增强",
        [SoundQualityMode.Custom] = "自定义",
    };

    public static readonly IReadOnlyDictionary<byte, string> GestureLabels = new Dictionary<byte, string>
    {
        [0x01] = "左耳双击",
        [0x03] = "左耳三击",
        [0x08] = "左耳长按",
        [0x02] = "右耳双击",
        [0x04] = "右耳三击",
        [0x09] = "右耳长按",
    };

    public static readonly byte[] TouchReadOrder = [0x01, 0x03, 0x08, 0x02, 0x04, 0x09];

    public static readonly IReadOnlyDictionary<TouchFunction, string> TouchFunctionLabels = new Dictionary<TouchFunction, string>
    {
        [TouchFunction.None] = "无",
        [TouchFunction.PlayPause] = "播放/暂停",
        [TouchFunction.Previous] = "上一曲",
        [TouchFunction.Next] = "下一曲",
        [TouchFunction.VolumeUp] = "音量加",
        [TouchFunction.VolumeDown] = "音量减",
        [TouchFunction.NoiseMode] = "降噪模式轮换",
        [TouchFunction.SceneSelect] = "场景选择",
        [TouchFunction.VoiceAssistant] = "语音助手",
    };

    public static readonly IReadOnlyDictionary<NoiseMode, byte> AncCycleBits = new Dictionary<NoiseMode, byte>
    {
        [NoiseMode.Deep] = 0x01,
        [NoiseMode.Comfort] = 0x02,
        [NoiseMode.Wide] = 0x04,
        [NoiseMode.Wind] = 0x08,
        [NoiseMode.Ambient] = 0x10,
        [NoiseMode.Off] = 0x20,
    };

    private readonly FitBudsClient _client;

    public FitBudsProtocol(FitBudsClient client) => _client = client;

    public async Task<(NoiseMode? Mode, int? AmbientDb, string Text)> QueryNoiseAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(0xCC00, attempts: attempts, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (packet is null)
        {
            return (null, null, "未读取到状态");
        }

        return DecodeNoise(packet.Payload);
    }

    public async Task<OperationResult> SetNoiseAsync(NoiseMode mode, CancellationToken cancellationToken = default)
    {
        var raw = (byte)mode;
        _ = await _client.RequestAsync(0xC100, new byte[] { 0x1B, raw }, cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var state = await QueryNoiseAsync(cancellationToken).ConfigureAwait(false);
            if (state.Mode == mode)
            {
                return OperationResult.Ok("降噪已更新");
            }
        }

        return OperationResult.Fail("降噪设置未生效");
    }

    public async Task<OperationResult> SetAmbientAsync(int db, CancellationToken cancellationToken = default)
    {
        if (db is < -3 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(db), "环境音级别超出可用范围");
        }

        var raw = (byte)(db + 6);
        _ = await _client.RequestAsync(0xC100, new byte[] { 0x1B, 0x05, raw }, cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var state = await QueryNoiseAsync(cancellationToken).ConfigureAwait(false);
            if (state.Mode == NoiseMode.Ambient && state.AmbientDb == db)
            {
                return OperationResult.Ok("环境音已更新");
            }
        }

        return OperationResult.Fail("环境音设置未生效");
    }

    public async Task<(SceneMode? Mode, string Text)> QuerySceneAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(0x0800, attempts: attempts, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (packet is null || packet.Payload.Length == 0)
        {
            return (null, "未读取到状态");
        }

        var value = packet.Payload[0];
        if (Enum.IsDefined(typeof(SceneMode), value))
        {
            var mode = (SceneMode)value;
            return (mode, SceneLabels[mode]);
        }

        return (null, "未知场景");
    }

    public async Task<OperationResult> SetSceneAsync(SceneMode scene, CancellationToken cancellationToken = default)
    {
        _ = await _client.RequestAsync(0x0900, new byte[] { (byte)scene }, cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var state = await QuerySceneAsync(cancellationToken).ConfigureAwait(false);
            if (state.Mode == scene)
            {
                return OperationResult.Ok("声音场景已更新");
            }
        }

        return OperationResult.Fail("声音场景设置未生效");
    }

    public async Task<(SoundQualityMode? Mode, string Text)> QuerySoundQualityAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            SoundQualityReadCategory,
            attempts: attempts,
            matcher: p => p.Payload.Length == 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (packet is null || packet.Payload.Length != 1)
        {
            return (null, "未读取到状态");
        }

        var raw = packet.Payload[0];
        if (!Enum.IsDefined(typeof(SoundQualityMode), raw))
        {
            return (null, "未知音质模式");
        }

        var mode = (SoundQualityMode)raw;
        return (mode, SoundQualityLabels[mode]);
    }

    public async Task<OperationResult> SetSoundQualityAsync(
        SoundQualityMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(SoundQualityMode), mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "音质模式超出可用范围");
        }

        // 0xC400 has no observed same-category ACK. Confirm it with the documented
        // 0xD500 status interface instead of waiting for a response that may never arrive.
        await _client.SendAsync(
            SoundQualityWriteCategory,
            new byte[] { (byte)mode },
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var state = await QuerySoundQualityAsync(cancellationToken, attempts: 1).ConfigureAwait(false);
            if (state.Mode == mode)
            {
                return OperationResult.Ok("音质设置已更新");
            }
        }

        return OperationResult.Fail("音质设置未生效");
    }

    /// <summary>
    /// Reads the four-band parametric EQ reported by category 0x4300.
    /// Observed payload layout:
    ///   0E 04 + (band, 02, freq_be16, gain_raw, q_raw) x 4 + 10-byte opaque trailer.
    /// Only the four confirmed band records are interpreted; the prefix/trailer are left opaque.
    /// </summary>
    public async Task<IReadOnlyList<EqualizerBand>> QueryEqualizerAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            EqualizerReadCategory,
            attempts: attempts,
            matcher: p => p.Payload.Length >= 2 + (EqualizerBandCount * 6) && p.Payload[1] == EqualizerBandCount,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return packet is null
            ? Array.Empty<EqualizerBand>()
            : DecodeEqualizer(packet.Payload);
    }

    /// <summary>
    /// Writes one or more EQ bands through category 0x4400. Each write payload is:
    ///   band, 00, freq_be16, gain_raw, q_raw
    /// where gain_raw 0..12 maps to -3..+3 dB in 0.5 dB steps and q_raw 7..20 maps to Q 0.7..2.0.
    /// The device acknowledges each band with payload 01; a final 0x4300 read verifies the values.
    /// </summary>
    public async Task<OperationResult> SetEqualizerAsync(
        IReadOnlyCollection<EqualizerBand> bands,
        CancellationToken cancellationToken = default)
    {
        var ordered = bands.OrderBy(band => band.BandIndex).ToArray();
        if (ordered.Length is < 1 or > EqualizerBandCount ||
            ordered.Select(band => band.BandIndex).Distinct().Count() != ordered.Length ||
            ordered.Any(band => band.BandIndex >= EqualizerBandCount))
        {
            throw new ArgumentException("均衡器频段编号必须是 0 到 3，且不能重复", nameof(bands));
        }

        foreach (var band in ordered)
        {
            var payload = EncodeEqualizerBand(band);
            var ack = await _client.RequestAsync(
                EqualizerWriteCategory,
                payload,
                matcher: p => p.Payload.Length >= 1 && p.Payload[0] == 0x01,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (ack is null)
            {
                return OperationResult.Fail($"频段 {band.BandIndex + 1} 未收到设置回应");
            }
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var readBack = await QueryEqualizerAsync(cancellationToken).ConfigureAwait(false);
        if (!EqualizerMatches(ordered, readBack))
        {
            return OperationResult.Fail("均衡器设置未生效");
        }

        return OperationResult.Ok("均衡器已更新");
    }

    public async Task<(bool? Enabled, int? Volume, string Text)> QueryPromptAsync(CancellationToken cancellationToken = default)
    {
        var packet = await _client.QueryRetryAsync(
            0x8600,
            attempts: 3,
            matcher: p => p.Payload.Length >= 3 && p.Payload[0] == 0x04,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (packet is null)
        {
            return (null, null, "未读取到状态");
        }

        var enabled = packet.Payload[1] switch { 0 => false, 1 => true, _ => (bool?)null };
        var sw = enabled switch { true => "已开启", false => "已关闭", null => "未知状态" };
        return (enabled, packet.Payload[2], $"{sw} 音量 {packet.Payload[2]}");
    }

    public async Task<OperationResult> SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var value = (byte)(enabled ? 1 : 0);
        _ = await _client.RequestAsync(0x8700, new byte[] { 0x04, 0x01, value }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var state = await QueryPromptAsync(cancellationToken).ConfigureAwait(false);
        return state.Enabled == enabled
            ? OperationResult.Ok("提示音已更新")
            : OperationResult.Fail("提示音设置未生效");
    }

    public async Task<OperationResult> SetPromptVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        if (volume is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "提示音音量超出可用范围");
        }

        // 06 00 + volume has been confirmed on the target device. It does not need a
        // same-category acknowledgement, so verify the result through the confirmed
        // 05 00 readback instead.
        await _client.SendAsync(
            PromptVolumeWriteCategory,
            new byte[] { (byte)volume },
            cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            var readBack = await QueryLegacyPromptVolumeAsync(cancellationToken, attempts: 1).ConfigureAwait(false);
            if (readBack == volume)
            {
                return OperationResult.Ok("提示音音量已更新");
            }
        }

        return OperationResult.Fail("提示音音量设置未生效");
    }

    public async Task<OperationResult> PlayMediaAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(MediaControlCategory, new byte[] { 0x00 }, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("播放指令已发送");
    }

    public async Task<OperationResult> PauseMediaAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(MediaControlCategory, new byte[] { 0x01 }, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("暂停指令已发送");
    }

    public async Task<int?> QueryTimerAsync(CancellationToken cancellationToken = default)
    {
        var packet = await _client.QueryRetryAsync(
            0xD300,
            attempts: 3,
            matcher: p => p.Payload.Length == 2,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return packet is null ? null : (packet.Payload[0] << 8) | packet.Payload[1];
    }

    public async Task<OperationResult> SetTimerAsync(int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < 1 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "定时时间需为 1 到 90 分钟");
        }

        // D100 carries the requested number of minutes as a two-byte big-endian value.
        // The original reference program only exposed a few confirmed presets; the
        // desktop UI deliberately allows the full 1..90 range and still verifies the
        // value through D300 so unsupported values fail cleanly instead of being assumed.
        var payload = new byte[] { (byte)(minutes >> 8), (byte)(minutes & 0xFF) };
        _ = await _client.RequestAsync(0xD100, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        var value = await QueryTimerAsync(cancellationToken).ConfigureAwait(false);
        return value == minutes
            ? OperationResult.Ok("定时已更新")
            : OperationResult.Fail("定时设置未生效");
    }

    public async Task<OperationResult> CancelTimerAsync(CancellationToken cancellationToken = default)
    {
        _ = await _client.RequestAsync(0xD200, cancellationToken).ConfigureAwait(false);
        var value = await QueryTimerAsync(cancellationToken).ConfigureAwait(false);
        return value == 0
            ? OperationResult.Ok("定时已关闭")
            : OperationResult.Fail("定时设置未生效");
    }

    public async Task<TouchAssignment?> QueryTouchAsync(byte gestureId, CancellationToken cancellationToken = default)
    {
        var packet = await _client.QueryRetryAsync(
            0xF000,
            new byte[] { gestureId },
            attempts: 3,
            matcher: p => p.Payload.Length >= 2 && p.Payload[0] == gestureId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return packet is null ? null : DecodeTouch(packet.Payload);
    }

    public async Task<IReadOnlyList<TouchAssignment>> QueryAllTouchAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<TouchAssignment>();
        foreach (var gestureId in TouchReadOrder)
        {
            var row = await QueryTouchAsync(gestureId, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    public async Task<OperationResult> SetTouchAsync(byte gestureId, TouchFunction function, CancellationToken cancellationToken = default)
    {
        var payload = new byte[] { gestureId, (byte)function };
        _ = await _client.RequestAsync(
            0xF100,
            payload,
            matcher: p => p.Payload.Length >= 2 && p.Payload[0] == gestureId && p.Payload[1] == (byte)function,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var readBack = await QueryTouchAsync(gestureId, cancellationToken).ConfigureAwait(false);
            if (readBack?.Function == function)
            {
                return OperationResult.Ok("触控设置已更新");
            }
        }

        return OperationResult.Fail("触控设置未生效");
    }

    public async Task<(IReadOnlyList<TouchAssignment> NoiseRows, IReadOnlyList<byte> Masks, byte? SharedMask)> QueryCycleStateAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAllTouchAsync(cancellationToken).ConfigureAwait(false);
        return GetCycleState(rows);
    }

    public async Task<OperationResult> SetCycleMaskAsync(IReadOnlyCollection<NoiseMode> modes, CancellationToken cancellationToken = default)
    {
        var unique = modes.Distinct().ToArray();
        if (unique.Length is < 2 or > 3)
        {
            throw new ArgumentException("请选择 2 到 3 个降噪模式", nameof(modes));
        }

        var mask = EncodeCycleMask(unique);
        var before = await QueryCycleStateAsync(cancellationToken).ConfigureAwait(false);
        if (before.NoiseRows.Count == 0)
        {
            return OperationResult.Fail("请先把任一长按操作设为降噪模式轮换");
        }

        var gestureId = before.NoiseRows[0].GestureId;
        var payload = new byte[] { gestureId, (byte)TouchFunction.NoiseMode, mask };
        _ = await _client.RequestAsync(
            0xF100,
            payload,
            matcher: p => p.Payload.Length >= 3 && p.Payload.AsSpan(0, 3).SequenceEqual(payload),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await Task.Delay(160, cancellationToken).ConfigureAwait(false);
        var after = await QueryCycleStateAsync(cancellationToken).ConfigureAwait(false);
        var ok = after.NoiseRows.Count > 0 && after.NoiseRows.All(r => r.Extra == mask);
        var labels = unique.Select(m => NoiseLabels[m]);
        return ok
            ? OperationResult.Ok("轮换设置已更新")
            : OperationResult.Fail("轮换设置未生效");
    }

    public async Task<BatterySnapshot?> QueryBatteryAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            0xF200,
            attempts: attempts,
            matcher: p => p.Payload.Length == 6,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (packet is null)
        {
            return null;
        }

        return DecodeBattery(packet.Payload);
    }

    public static BatterySnapshot? DecodeBattery(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 6)
        {
            return null;
        }

        var mask = payload[0];
        int? left = (mask & 0x01) != 0 && payload[1] != 0 ? payload[1] : null;
        int? right = (mask & 0x02) != 0 && payload[2] != 0 ? payload[2] : null;
        int? batteryCase = payload[3] != 0 ? payload[3] : null;
        return new BatterySnapshot(mask, left, right, batteryCase, payload[4], payload[5]);
    }

    public async Task<string?> QueryMacAsync(CancellationToken cancellationToken = default)
    {
        var packet = await _client.QueryRetryAsync(
            0xC800,
            attempts: 3,
            matcher: p => p.Payload.Length == 6,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return packet is null ? null : string.Join(":", packet.Payload.Select(b => b.ToString("X2")));
    }

    public async Task<string?> QueryNameAsync(CancellationToken cancellationToken = default)
    {
        var packet = await _client.QueryRetryAsync(
            0xC900,
            matcher: p => p.Payload.Length > 0,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return packet is null ? null : Encoding.UTF8.GetString(packet.Payload).Trim('\0', ' ');
    }

    public async Task<int?> QueryLegacyBatteryAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            LegacyBatteryReadCategory,
            attempts: attempts,
            matcher: p => p.Payload.Length == 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (packet is null || packet.Payload[0] > 100)
        {
            return null;
        }

        return packet.Payload[0];
    }

    public async Task<string?> QueryFirmwareVersionAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            FirmwareReadCategory,
            attempts: attempts,
            matcher: p => p.Payload.Length == 3,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return packet is null
            ? null
            : string.Join(".", packet.Payload.Select(value => value.ToString("X")));
    }

    public async Task<int?> QueryLegacyPromptVolumeAsync(
        CancellationToken cancellationToken = default,
        int attempts = 3)
    {
        var packet = await _client.QueryRetryAsync(
            LegacyPromptVolumeReadCategory,
            attempts: attempts,
            matcher: p => p.Payload.Length == 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return packet?.Payload[0];
    }

    public async Task<OperationResult> PowerOffDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(DevicePowerOffCategory, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("关机指令已发送");
    }

    public async Task<OperationResult> DisconnectDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(DeviceDisconnectCategory, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("断开连接指令已发送");
    }

    public async Task<OperationResult> EnterPairingAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(DevicePairingCategory, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("进入配对指令已发送");
    }

    public async Task<OperationResult> ResetDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _client.SendAsync(DeviceResetCategory, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("重置指令已发送");
    }

    public async Task<OperationResult> SetDeviceNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return OperationResult.Fail("设备名称不能为空");
        }

        var bytes = Encoding.UTF8.GetBytes(trimmed);
        if (bytes.Length > DeviceNameMaxUtf8Bytes)
        {
            return OperationResult.Fail("设备名称太长，请缩短后重试");
        }

        var response = await _client.RequestAsync(
            DeviceRenameCategory,
            bytes,
            matcher: packet => packet.Payload.Length == 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return response?.Payload.Length == 1 && response.Payload[0] == 0x01
            ? OperationResult.Ok("设备名称已更新")
            : OperationResult.Fail("设备没有确认名称修改");
    }

    public async Task<EarbudsState> QueryConfirmedStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = new EarbudsState { IsConnected = _client.IsConnected };

        var noise = await QueryNoiseAsync(cancellationToken).ConfigureAwait(false);
        state.NoiseMode = noise.Mode;
        state.AmbientDb = noise.AmbientDb;

        var scene = await QuerySceneAsync(cancellationToken).ConfigureAwait(false);
        state.SceneMode = scene.Mode;

        var soundQuality = await QuerySoundQualityAsync(cancellationToken).ConfigureAwait(false);
        state.SoundQualityMode = soundQuality.Mode;

        state.EqualizerBands = await QueryEqualizerAsync(cancellationToken).ConfigureAwait(false);

        var prompt = await QueryPromptAsync(cancellationToken).ConfigureAwait(false);
        var legacyPromptVolume = await QueryLegacyPromptVolumeAsync(cancellationToken, attempts: 1).ConfigureAwait(false);
        state.PromptEnabled = prompt.Enabled;
        state.PromptVolume = legacyPromptVolume ?? prompt.Volume;

        state.TimerMinutes = await QueryTimerAsync(cancellationToken).ConfigureAwait(false);

        var battery = await QueryBatteryAsync(cancellationToken).ConfigureAwait(false);
        if (battery is not null)
        {
            state.OnlineMask = battery.OnlineMask;
            state.LeftBattery = battery.LeftPercent;
            state.RightBattery = battery.RightPercent;
            state.CaseBattery = battery.CasePercent;
            state.CaseState = battery.CaseState;
            state.IsCaseCharging = battery.IsCaseCharging;
        }

        state.DeviceMac = await QueryMacAsync(cancellationToken).ConfigureAwait(false) ?? _client.ConnectedMac ?? string.Empty;
        state.DeviceName = await QueryNameAsync(cancellationToken).ConfigureAwait(false) ?? "EDIFIER FitBuds Turbo";
        state.FirmwareVersion = await QueryFirmwareVersionAsync(cancellationToken, attempts: 1).ConfigureAwait(false) ?? string.Empty;
        state.TouchAssignments = await QueryAllTouchAsync(cancellationToken).ConfigureAwait(false);

        var cycle = GetCycleState(state.TouchAssignments);
        state.ObservedAncCycleMasks = cycle.Masks;
        state.SharedAncCycleMask = cycle.SharedMask;
        state.LastRefresh = DateTimeOffset.Now;
        state.ConnectionDetail = "已连接";

        return state;
    }

    public static byte EncodeCycleMask(IEnumerable<NoiseMode> modes)
    {
        var unique = modes.Distinct().ToArray();
        if (unique.Length is < 2 or > 3)
        {
            throw new ArgumentException("请选择 2 到 3 个降噪模式", nameof(modes));
        }

        byte mask = 0;
        foreach (var mode in unique)
        {
            mask |= AncCycleBits[mode];
        }
        return mask;
    }

    public static IReadOnlyList<NoiseMode> DecodeCycleMask(byte mask)
        => AncCycleBits.Where(kv => (mask & kv.Value) != 0).Select(kv => kv.Key).ToArray();

    internal static (NoiseMode? Mode, int? AmbientDb, string Text) DecodeNoise(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0x1B)
        {
            return (null, null, "未知状态");
        }

        var raw = payload[1];
        if (!Enum.IsDefined(typeof(NoiseMode), raw))
        {
            return (null, null, "未知状态");
        }

        var mode = (NoiseMode)raw;
        int? db = null;
        if (mode == NoiseMode.Ambient && payload.Length >= 3 && payload[2] is >= 0x03 and <= 0x09)
        {
            db = payload[2] - 6;
        }

        var text = NoiseLabels[mode];
        if (db is not null)
        {
            text += $" {db.Value:+0;-0;0}";
        }
        return (mode, db, text);
    }

    internal static IReadOnlyList<EqualizerBand> DecodeEqualizer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 + (EqualizerBandCount * 6) || payload[1] != EqualizerBandCount)
        {
            return Array.Empty<EqualizerBand>();
        }

        var bands = new List<EqualizerBand>(EqualizerBandCount);
        var offset = 2;
        for (var i = 0; i < EqualizerBandCount; i++, offset += 6)
        {
            var bandIndex = payload[offset];
            var frequency = (payload[offset + 2] << 8) | payload[offset + 3];
            var gain = (payload[offset + 4] - 6) / 2.0;
            var q = payload[offset + 5] / 10m;

            if (bandIndex >= EqualizerBandCount ||
                bands.Any(band => band.BandIndex == bandIndex) ||
                !IsEqualizerFrequencyValid(bandIndex, frequency))
            {
                return Array.Empty<EqualizerBand>();
            }

            bands.Add(new EqualizerBand(bandIndex, frequency, gain, q));
        }

        return bands.OrderBy(band => band.BandIndex).ToArray();
    }

    private static byte[] EncodeEqualizerBand(EqualizerBand band)
    {
        if (band.BandIndex >= EqualizerBandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(band), "均衡器频段编号超出可用范围");
        }
        var (minFrequencyHz, maxFrequencyHz) = GetEqualizerFrequencyRange(band.BandIndex);
        if (band.FrequencyHz < minFrequencyHz || band.FrequencyHz > maxFrequencyHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(band),
                $"频段 {band.BandIndex + 1} 的频率需为 {minFrequencyHz} 到 {maxFrequencyHz} Hz");
        }

        var normalizedGain = Math.Round(band.GainDb * 2.0, MidpointRounding.AwayFromZero) / 2.0;
        if (normalizedGain < EqualizerMinGainDb || normalizedGain > EqualizerMaxGainDb ||
            Math.Abs(normalizedGain - band.GainDb) > 0.001)
        {
            throw new ArgumentOutOfRangeException(nameof(band), "增益需为 -3 到 +3 dB，步进 0.5 dB");
        }

        var normalizedQ = decimal.Round(band.Q, 1, MidpointRounding.AwayFromZero);
        if (normalizedQ < EqualizerMinQ || normalizedQ > EqualizerMaxQ || normalizedQ != band.Q)
        {
            throw new ArgumentOutOfRangeException(nameof(band), "Q 值需为 0.7 到 2.0，步进 0.1");
        }

        var gainRaw = checked((byte)Math.Round((normalizedGain * 2.0) + 6.0, MidpointRounding.AwayFromZero));
        var qRaw = checked((byte)decimal.Round(normalizedQ * 10m, 0, MidpointRounding.AwayFromZero));
        return
        [
            band.BandIndex,
            0x00,
            (byte)(band.FrequencyHz >> 8),
            (byte)(band.FrequencyHz & 0xFF),
            gainRaw,
            qRaw,
        ];
    }

    private static bool EqualizerMatches(
        IReadOnlyCollection<EqualizerBand> expected,
        IReadOnlyCollection<EqualizerBand> actual)
    {
        if (expected.Count == 0 || actual.Count != EqualizerBandCount)
        {
            return false;
        }

        var actualByIndex = actual.ToDictionary(band => band.BandIndex);
        foreach (var band in expected)
        {
            if (!actualByIndex.TryGetValue(band.BandIndex, out var readBack) ||
                readBack.FrequencyHz != band.FrequencyHz ||
                Math.Abs(readBack.GainDb - band.GainDb) > 0.001 ||
                readBack.Q != band.Q)
            {
                return false;
            }
        }

        return true;
    }

    internal static TouchAssignment DecodeTouch(ReadOnlySpan<byte> payload)
    {
        var gestureId = payload[0];
        var functionId = payload[1];
        var gesture = GestureLabels.TryGetValue(gestureId, out var gl) ? gl : "未知操作";

        TouchFunction? function = Enum.IsDefined(typeof(TouchFunction), functionId)
            ? (TouchFunction)functionId
            : null;
        var functionLabel = function is not null && TouchFunctionLabels.TryGetValue(function.Value, out var fl)
            ? fl
            : "未知功能";

        byte? extra = payload.Length >= 3 ? payload[2] : null;
        return new TouchAssignment(gestureId, gesture, function, functionLabel, extra);
    }

    private static (IReadOnlyList<TouchAssignment> NoiseRows, IReadOnlyList<byte> Masks, byte? SharedMask) GetCycleState(
        IReadOnlyList<TouchAssignment> rows)
    {
        var noiseRows = rows.Where(r => r.Function == TouchFunction.NoiseMode && r.Extra is not null).ToArray();
        var masks = noiseRows.Select(r => r.Extra!.Value).Distinct().Order().ToArray();
        byte? shared = masks.Length == 1 ? masks[0] : null;
        return (noiseRows, masks, shared);
    }
}
