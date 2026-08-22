using System.Text;
using System.Text.RegularExpressions;
using FitBudsControl.Models;

namespace FitBudsControl.Bluetooth;

public enum ProtocolTrafficDirection
{
    ToDevice,
    FromDevice,
}

public sealed record ProtocolTrafficEntry(
    DateTimeOffset Timestamp,
    ProtocolTrafficDirection Direction,
    byte[] Raw);

public sealed record ProtocolLogDisplayLine(
    string TimeText,
    ProtocolTrafficDirection Direction,
    string Arrow,
    string FrameText,
    string Description);

public static class ProtocolLogFormatter
{
    private static readonly Regex PacketRegex = new(
        @"(?ix)(?<![0-9a-f])(?<packet>(?:aa|bb)(?:(?:[ \t,:;|_\-]*)[0-9a-f]{2}){5,260})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ByteRegex = new(
        @"(?i)[0-9a-f]{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProtocolLogDisplayLine BuildLiveDisplayLine(ProtocolTrafficEntry entry)
    {
        var arrow = entry.Direction == ProtocolTrafficDirection.ToDevice ? "-->" : "<--";
        return new ProtocolLogDisplayLine(
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.Direction,
            arrow,
            FormatFrame(entry.Raw),
            Describe(entry.Raw) ?? string.Empty);
    }

    public static ProtocolLogDisplayLine BuildExtractedDisplayLine(byte[] raw)
    {
        var direction = raw.Length > 0 && raw[0] == FitBudsFrameCodec.ResponseHeader0
            ? ProtocolTrafficDirection.FromDevice
            : ProtocolTrafficDirection.ToDevice;
        var arrow = direction == ProtocolTrafficDirection.ToDevice ? "-->" : "<--";
        return new ProtocolLogDisplayLine(
            string.Empty,
            direction,
            arrow,
            FormatFrame(raw),
            Describe(raw) ?? string.Empty);
    }

    public static IReadOnlyList<byte[]> ExtractFrames(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<byte[]>();
        }

        var output = new List<byte[]>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (var line in lines)
        {
            foreach (Match match in PacketRegex.Matches(line))
            {
                var byteMatches = ByteRegex.Matches(match.Groups["packet"].Value);
                if (byteMatches.Count < 6)
                {
                    continue;
                }

                var bytes = new byte[byteMatches.Count];
                for (var index = 0; index < byteMatches.Count; index++)
                {
                    bytes[index] = Convert.ToByte(byteMatches[index].Value, 16);
                }

                var offset = 0;
                while (offset + 6 <= bytes.Length)
                {
                    while (offset < bytes.Length && bytes[offset] is not (FitBudsFrameCodec.RequestHeader0 or FitBudsFrameCodec.ResponseHeader0))
                    {
                        offset++;
                    }

                    if (offset + 6 > bytes.Length)
                    {
                        break;
                    }

                    var frameLength = 6 + bytes[offset + 4];
                    if (offset + frameLength > bytes.Length)
                    {
                        break;
                    }

                    var frame = bytes.AsSpan(offset, frameLength).ToArray();
                    if (frame[0] is FitBudsFrameCodec.RequestHeader0 or FitBudsFrameCodec.ResponseHeader0)
                    {
                        output.Add(frame);
                    }
                    offset += frameLength;
                }
            }
        }

        return output;
    }

    private static string FormatFrame(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 6)
        {
            return Hex(raw);
        }

        var header = Hex(raw[..5]);
        var payload = raw.Length > 6 ? Hex(raw[5..^1]) : string.Empty;
        var checksum = raw[^1].ToString("X2");
        return payload.Length == 0
            ? $"{header}  {checksum}"
            : $"{header}  {payload}  {checksum}";
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
        => string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2")));

    private static string? Describe(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 6)
        {
            return null;
        }

        var payloadLength = raw[4];
        if (raw.Length < 6 + payloadLength || FitBudsFrameCodec.Checksum(raw[..^1]) != raw[^1])
        {
            return null;
        }

        var toDevice = raw[0] == FitBudsFrameCodec.RequestHeader0;
        var fromDevice = raw[0] == FitBudsFrameCodec.ResponseHeader0;
        if (!toDevice && !fromDevice)
        {
            return null;
        }

        var category = (ushort)((raw[2] << 8) | raw[3]);
        var payload = raw.Slice(5, payloadLength);

        return category switch
        {
            0xCC00 => DescribeNoiseStatus(toDevice, payload),
            0xC100 => DescribeNoiseSet(toDevice, payload),
            0x0800 => DescribeSceneStatus(toDevice, payload),
            0x0900 => DescribeSceneSet(toDevice, payload),
            FitBudsProtocol.SoundQualityReadCategory => DescribeSoundQualityStatus(toDevice, payload),
            FitBudsProtocol.SoundQualityWriteCategory => DescribeSoundQualitySet(toDevice, payload),
            FitBudsProtocol.EqualizerReadCategory => DescribeEqualizerStatus(toDevice, payload),
            FitBudsProtocol.EqualizerWriteCategory => DescribeEqualizerSet(toDevice, payload),
            0x8600 => DescribePromptStatus(toDevice, payload),
            0x8700 => DescribePromptSet(toDevice, payload),
            0xD300 => DescribeTimerStatus(toDevice, payload),
            0xD100 => DescribeTimerSet(toDevice, payload),
            0xD200 => toDevice ? "关闭定时" : "关闭定时回应",
            0xF000 => DescribeTouchStatus(toDevice, payload),
            0xF100 => DescribeTouchSet(toDevice, payload),
            0xF200 => DescribeBattery(toDevice, payload),
            FitBudsProtocol.LegacyBatteryReadCategory => DescribeLegacyBattery(toDevice, payload),
            FitBudsProtocol.FirmwareReadCategory => DescribeFirmware(toDevice, payload),
            FitBudsProtocol.LegacyPromptVolumeReadCategory => DescribeLegacyPromptVolume(toDevice, payload),
            FitBudsProtocol.PromptVolumeWriteCategory => DescribePromptVolumeSet(toDevice, payload),
            FitBudsProtocol.MediaControlCategory => DescribeMediaControl(toDevice, payload),
            0x6800 => DescribePossibleSwitch(toDevice, payload),
            0x9100 => DescribePossibleFeatureState(toDevice, payload),
            0xC300 => DescribePlaybackState(toDevice, payload),
            0xD800 => DescribeDeviceCapabilityInfo(toDevice, payload),
            0xFB00 => DescribeWearDetection(toDevice, payload),
            0xFD00 => DescribeTouchSensitivity(toDevice, payload),
            FitBudsProtocol.DevicePowerOffCategory => toDevice ? "设备关机" : "设备关机回应",
            FitBudsProtocol.DeviceDisconnectCategory => toDevice ? "设备断开连接" : "设备断开连接回应",
            FitBudsProtocol.DevicePairingCategory => toDevice ? "设备进入配对" : "设备进入配对回应",
            FitBudsProtocol.DeviceResetCategory => toDevice ? "设备重置" : "设备重置回应",
            FitBudsProtocol.DeviceRenameCategory => DescribeDeviceRename(toDevice, payload),
            0xC800 => toDevice ? "读取耳机地址" : "耳机地址回应",
            0xC900 => DescribeDeviceName(toDevice, payload),
            _ => DescribeLegacyCandidate(category, toDevice, payload),
        };
    }

    private static string? DescribeLegacyCandidate(ushort category, bool toDevice, ReadOnlySpan<byte> payload)
    {
        var candidates = LegacyEdifierCommandCatalog.All.Where(item => item.Category == category).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (!toDevice)
        {
            return $"收到：{candidates[0].Group} · {candidates[0].Name}";
        }

        var payloadHex = Hex(payload);
        var exact = candidates.FirstOrDefault(item =>
            string.Equals(item.PayloadHex.Trim(), payloadHex, StringComparison.OrdinalIgnoreCase));
        var selected = exact ?? candidates[0];
        return $"手动发送：{selected.Group} · {selected.Name}";
    }

    private static string DescribeNoiseStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取降噪状态";
        }

        var decoded = FitBudsProtocol.DecodeNoise(payload);
        if (decoded.Mode is null)
        {
            return "降噪状态回应";
        }

        return decoded.AmbientDb is int db
            ? $"降噪状态：{FitBudsProtocol.NoiseLabels[decoded.Mode.Value]} {db:+0;-0;0}"
            : $"降噪状态：{FitBudsProtocol.NoiseLabels[decoded.Mode.Value]}";
    }

    private static string DescribeNoiseSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "降噪设置回应";
        }

        if (payload.Length >= 3 && payload[0] == 0x1B && payload[1] == (byte)NoiseMode.Ambient)
        {
            return $"设置环境音强度为 {payload[2] - 6:+0;-0;0}";
        }

        if (payload.Length >= 2 && payload[0] == 0x1B && Enum.IsDefined(typeof(NoiseMode), payload[1]))
        {
            var mode = (NoiseMode)payload[1];
            return $"设置降噪为{FitBudsProtocol.NoiseLabels[mode]}";
        }

        return "设置降噪";
    }

    private static string DescribeSceneStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取声音场景";
        }

        if (payload.Length > 0 && Enum.IsDefined(typeof(SceneMode), payload[0]))
        {
            return $"声音场景：{FitBudsProtocol.SceneLabels[(SceneMode)payload[0]]}";
        }

        return "声音场景回应";
    }

    private static string DescribeSceneSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "声音场景设置回应";
        }

        if (payload.Length > 0 && Enum.IsDefined(typeof(SceneMode), payload[0]))
        {
            return $"设置声音场景为{FitBudsProtocol.SceneLabels[(SceneMode)payload[0]]}";
        }

        return "设置声音场景";
    }

    private static string DescribeSoundQualityStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取音质设置";
        }

        if (payload.Length == 1 && Enum.IsDefined(typeof(SoundQualityMode), payload[0]))
        {
            var mode = (SoundQualityMode)payload[0];
            return $"音质：{FitBudsProtocol.SoundQualityLabels[mode]}";
        }

        return "音质设置回应";
    }

    private static string DescribeSoundQualitySet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "音质设置回应";
        }

        if (payload.Length == 1 && Enum.IsDefined(typeof(SoundQualityMode), payload[0]))
        {
            var mode = (SoundQualityMode)payload[0];
            return $"设置音质为{FitBudsProtocol.SoundQualityLabels[mode]}";
        }

        return "设置音质";
    }

    private static string DescribeEqualizerStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取四段均衡器";
        }

        var bands = FitBudsProtocol.DecodeEqualizer(payload);
        if (bands.Count != FitBudsProtocol.EqualizerBandCount)
        {
            return "均衡器参数回应";
        }

        var summary = bands.Select(band =>
            $"{band.BandIndex + 1}:{band.FrequencyHz}Hz {band.GainDb:+0.0;-0.0;0.0}dB Q{band.Q:0.0}");
        return $"均衡器：{string.Join(" / ", summary)}";
    }

    private static string DescribeEqualizerSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return payload.Length > 0 && payload[0] == 0x01
                ? "均衡器设置回应：成功"
                : "均衡器设置回应";
        }

        if (payload.Length != 6)
        {
            return "设置均衡器";
        }

        var bandIndex = payload[0];
        var frequency = (payload[2] << 8) | payload[3];
        var gain = (payload[4] - 6) / 2.0;
        var q = payload[5] / 10m;
        return $"设置均衡器频段 {bandIndex + 1}：{frequency}Hz {gain:+0.0;-0.0;0.0}dB Q{q:0.0}";
    }

    private static string DescribePromptStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取提示音状态";
        }

        if (payload.Length >= 3 && payload[0] == 0x04)
        {
            var enabled = payload[1] == 0x01 ? "开启" : payload[1] == 0x00 ? "关闭" : "未知";
            return $"提示音：{enabled} 音量 {payload[2]}";
        }

        return "提示音状态回应";
    }

    private static string DescribePromptSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "提示音设置回应";
        }

        if (payload.Length >= 3 && payload[0] == 0x04 && payload[1] == 0x01)
        {
            return payload[2] == 0x01 ? "开启提示音" : "关闭提示音";
        }

        if (payload.Length >= 3 && payload[0] == 0x04 && payload[1] == 0x02)
        {
            return $"设置提示音音量为 {payload[2]}";
        }

        return "设置提示音";
    }

    private static string DescribeTimerStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取定时状态";
        }

        if (payload.Length == 2)
        {
            var minutes = (payload[0] << 8) | payload[1];
            return minutes == 0 ? "定时已关闭" : $"定时剩余 {minutes} 分钟";
        }

        return "定时状态回应";
    }

    private static string DescribeTimerSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "定时设置回应";
        }

        if (payload.Length == 2)
        {
            var minutes = (payload[0] << 8) | payload[1];
            return $"设置 {minutes} 分钟后关闭";
        }

        return "设置定时关闭";
    }

    private static string DescribeTouchStatus(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return payload.Length > 0 && FitBudsProtocol.GestureLabels.TryGetValue(payload[0], out var gesture)
                ? $"读取{gesture}设置"
                : "读取触控设置";
        }

        if (payload.Length >= 2)
        {
            var assignment = FitBudsProtocol.DecodeTouch(payload);
            return $"{assignment.GestureLabel}：{assignment.FunctionLabel}";
        }

        return "触控状态回应";
    }

    private static string DescribeTouchSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            var looksLikeKnownSetting = payload.Length >= 2 &&
                FitBudsProtocol.GestureLabels.ContainsKey(payload[0]) &&
                Enum.IsDefined(typeof(TouchFunction), payload[1]);
            if (!looksLikeKnownSetting)
            {
                return payload.Length == 0
                    ? "收到触控相关数据"
                    : $"收到触控相关数据：{Hex(payload)}（内容未识别，不代表设置成功）";
            }

            var returned = FitBudsProtocol.DecodeTouch(payload);
            return $"触控返回：{returned.GestureLabel} = {returned.FunctionLabel}";
        }

        if (payload.Length < 2)
        {
            return "发送触控设置（内容不完整）";
        }

        var assignment = FitBudsProtocol.DecodeTouch(payload);
        if (assignment.Function == TouchFunction.NoiseMode && assignment.Extra is byte mask)
        {
            var modes = FitBudsProtocol.DecodeCycleMask(mask)
                .Select(mode => FitBudsProtocol.NoiseLabels[mode]);
            var modeText = string.Join(" / ", modes);
            return modeText.Length == 0
                ? $"设置{assignment.GestureLabel}为{assignment.FunctionLabel}"
                : $"设置{assignment.GestureLabel}为{assignment.FunctionLabel}：{modeText}";
        }

        return $"设置{assignment.GestureLabel}为{assignment.FunctionLabel}";
    }

    private static string DescribeBattery(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取电量";
        }

        var snapshot = FitBudsProtocol.DecodeBattery(payload);
        if (snapshot is null)
        {
            return "电量回应";
        }

        var parts = new List<string>();
        if (snapshot.LeftPercent is int left)
        {
            parts.Add($"左耳 {left}%");
        }
        if (snapshot.RightPercent is int right)
        {
            parts.Add($"右耳 {right}%");
        }
        if (snapshot.CasePercent is int batteryCase)
        {
            parts.Add($"耳机盒 {batteryCase}%");
        }
        if (snapshot.IsCaseCharging)
        {
            parts.Add("耳机盒充电中");
        }

        return parts.Count == 0 ? "电量回应" : $"电量：{string.Join("  ", parts)}";
    }

    private static string DescribeLegacyBattery(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取设备总电量";
        }

        return payload.Length == 1
            ? $"设备总电量：{payload[0]}%"
            : "设备总电量返回";
    }

    private static string DescribeFirmware(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取固件版本";
        }

        return payload.Length == 0
            ? "固件版本回应"
            : $"固件版本：{string.Join(".", payload.ToArray().Select(value => value.ToString("X")))}";
    }

    private static string DescribeLegacyPromptVolume(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取提示音音量";
        }

        return payload.Length == 1
            ? $"提示音音量：{payload[0]}"
            : "提示音音量返回";
    }

    private static string DescribePromptVolumeSet(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "提示音音量设置回应";
        }

        return payload.Length == 1
            ? $"设置提示音音量为 {payload[0]}"
            : "设置提示音音量";
    }

    private static string DescribeMediaControl(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return "播放控制回应";
        }

        if (payload.Length != 1)
        {
            return "发送播放控制";
        }

        return payload[0] switch
        {
            0x00 => "播放",
            0x01 => "暂停",
            _ => $"发送尚未确认的播放控制值 {payload[0]:X2}",
        };
    }

    private static string DescribePossibleSwitch(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取未知开关状态（推测）";
        }

        return payload.Length == 1
            ? $"未知开关状态：{payload[0]:X2}（功能待确认，推测 00/01 表示关/开）"
            : $"未知开关返回：{Hex(payload)}（功能待确认）";
    }

    private static string DescribePossibleFeatureState(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取未知功能状态（推测）";
        }

        return payload.Length == 2
            ? $"未知功能状态：{payload[0]:X2} {payload[1]:X2}（具体含义待确认）"
            : $"未知功能状态返回：{Hex(payload)}（具体含义待确认）";
    }

    private static string DescribePlaybackState(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取播放状态";
        }

        if (payload.Length != 1)
        {
            return "播放状态返回";
        }

        return payload[0] switch
        {
            0x03 => "播放状态：已暂停",
            0x0D => "播放状态：正在播放",
            _ => $"播放状态返回：{payload[0]:X2}（这个值还没有确认）",
        };
    }

    private static string DescribeDeviceCapabilityInfo(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取设备型号与功能信息";
        }

        return payload.Length == 0
            ? "设备型号与功能信息返回"
            : $"设备型号与功能信息：{Hex(payload)}（各项含义还没有完全确认）";
    }

    private static string DescribeWearDetection(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取佩戴检测设置（推测）";
        }

        return payload.Length == 0
            ? "佩戴检测设置返回（推测）"
            : $"佩戴检测设置：{Hex(payload)}（两个值的对应关系待确认）";
    }

    private static string DescribeTouchSensitivity(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取触控灵敏度（推测，本机目前无回应）";
        }

        return payload.Length == 0
            ? "触控灵敏度返回（推测）"
            : $"触控灵敏度：{Hex(payload)}（具体范围待确认）";
    }

    private static string DescribeDeviceRename(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (!toDevice)
        {
            return payload.Length == 1 && payload[0] == 0x01
                ? "设备名称修改成功"
                : "设备名称修改返回";
        }

        var name = payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(payload).Trim('\0', ' ');
        return name.Length == 0 ? "修改设备名称" : $"修改设备名称为：{name}";
    }

    private static string DescribeDeviceName(bool toDevice, ReadOnlySpan<byte> payload)
    {
        if (toDevice)
        {
            return "读取耳机名称";
        }

        if (payload.Length == 0)
        {
            return "耳机名称回应";
        }

        var name = Encoding.UTF8.GetString(payload).Trim('\0', ' ');
        return name.Length == 0 ? "耳机名称回应" : $"耳机名称：{name}";
    }
}
