namespace FitBudsControl.Models;

public enum NoiseMode : byte
{
    Deep = 0x01,
    Comfort = 0x02,
    Wide = 0x03,
    Wind = 0x04,
    Ambient = 0x05,
    Off = 0x06,
}

public enum SceneMode : byte
{
    Music = 0x00,
    Game = 0x01,
    Spatial = 0x02,
}

public enum SoundQualityMode : byte
{
    Classic = 0x00,
    BassBoost = 0x01,
    ClearVoice = 0x02,
    TrebleBoost = 0x03,
    Custom = 0x04,
}


public enum EarbudsEventKind
{
    Connected,
    Disconnected,
    NoiseModeChanged,
    SoundModeChanged,
    LowBattery,
}

public sealed class EarbudsEventArgs : EventArgs
{
    public EarbudsEventArgs(EarbudsEventKind kind)
    {
        Kind = kind;
    }

    public EarbudsEventKind Kind { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

public enum TouchFunction : byte
{
    None = 0x01,
    PlayPause = 0x02,
    Previous = 0x03,
    Next = 0x04,
    VolumeUp = 0x05,
    VolumeDown = 0x06,
    NoiseMode = 0x07,
    SceneSelect = 0x08,
    VoiceAssistant = 0x09,
}

public sealed record BatterySnapshot(
    byte OnlineMask,
    int? LeftPercent,
    int? RightPercent,
    int? CasePercent,
    byte CaseState,
    byte Flags)
{
    // F200 byte4=0x01 was observed while the charging case had external power.
    public bool IsCaseCharging => CaseState == 0x01;
}

public sealed record TouchAssignment(
    byte GestureId,
    string GestureLabel,
    TouchFunction? Function,
    string FunctionLabel,
    byte? Extra);

public sealed record EqualizerBand(
    byte BandIndex,
    int FrequencyHz,
    double GainDb,
    decimal Q);

public sealed record OperationResult(bool Success, string Detail)
{
    public static OperationResult Ok(string detail) => new(true, detail);
    public static OperationResult Fail(string detail) => new(false, detail);
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

public sealed class ShortcutBinding
{
    public bool Enabled { get; set; } = true;
    public ShortcutModifiers Modifiers { get; set; } =
        ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift;
    public int Key { get; set; }
}

public sealed class EarbudsState
{
    public bool IsConnected { get; internal set; }
    public string ConnectionDetail { get; internal set; } = "未连接";

    public int? LeftBattery { get; internal set; }
    public int? RightBattery { get; internal set; }
    public int? CaseBattery { get; internal set; }
    public int? LegacyBatteryPercent { get; internal set; }
    public byte OnlineMask { get; internal set; }
    public byte? CaseState { get; internal set; }
    public bool IsCaseCharging { get; internal set; }

    public NoiseMode? NoiseMode { get; internal set; }
    public int? AmbientDb { get; internal set; }
    public SceneMode? SceneMode { get; internal set; }
    public SoundQualityMode? SoundQualityMode { get; internal set; }
    public IReadOnlyList<EqualizerBand> EqualizerBands { get; internal set; } = Array.Empty<EqualizerBand>();

    public bool? PromptEnabled { get; internal set; }
    public int? PromptVolume { get; internal set; }
    public int? TimerMinutes { get; internal set; }
    public DateTimeOffset? TimerDeadline { get; internal set; }

    public string DeviceName { get; internal set; } = "EDIFIER FitBuds Turbo";
    public string DeviceMac { get; internal set; } = string.Empty;
    public string FirmwareVersion { get; internal set; } = string.Empty;

    public IReadOnlyList<TouchAssignment> TouchAssignments { get; internal set; } = Array.Empty<TouchAssignment>();
    public byte? SharedAncCycleMask { get; internal set; }
    public IReadOnlyList<byte> ObservedAncCycleMasks { get; internal set; } = Array.Empty<byte>();

    public string LastError { get; internal set; } = string.Empty;
    public DateTimeOffset? LastRefresh { get; internal set; }
}

public sealed class AppSettings
{
    public string BluetoothMac { get; set; } = "6C:16:29:5A:01:CC";
    public int RfcommChannel { get; set; } = 10;
    public int ConnectTimeoutMs { get; set; } = 2000;
    public int RequestTimeoutMs { get; set; } = 450;
    public bool AutoConnectOnOpen { get; set; } = true;
    public int ReconnectIntervalSeconds { get; set; } = 15;

    public bool LowBatteryNotificationsEnabled { get; set; } = true;
    public int LeftLowBatteryThreshold { get; set; } = 20;
    public int RightLowBatteryThreshold { get; set; } = 20;
    public int CaseLowBatteryThreshold { get; set; } = 20;

    public bool StartWithWindows { get; set; }
    public bool AlwaysUseBlueTrayIcon { get; set; }
    public bool AutoUpdateCheckEnabled { get; set; } = true;

    // Three modifiers keep the defaults away from Windows and common application
    // shortcuts while still making the related commands easy to remember.
    public ShortcutBinding OpenPanelShortcut { get; set; } = new() { Key = 0x46 }; // F
    public ShortcutBinding NoiseModeShortcut { get; set; } = new() { Key = 0x4E }; // N
    public ShortcutBinding SoundSceneShortcut { get; set; } = new() { Key = 0x53 }; // S

    public bool AutoOpenPanelOnEvents { get; set; }
    public bool OpenPanelOnConnected { get; set; } = true;
    public bool OpenPanelOnDisconnected { get; set; }
    public bool OpenPanelOnNoiseModeChanged { get; set; } = true;
    public bool OpenPanelOnSoundModeChanged { get; set; } = true;
    public bool OpenPanelOnLowBattery { get; set; } = true;
}
