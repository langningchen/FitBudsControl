namespace FitBudsControl.Bluetooth;

public enum LegacyCommandConfidence
{
    Confirmed,
    Integrated,
    ExistingFamily,
    Candidate,
}

public sealed record LegacyEdifierCommandPreset(
    string Group,
    string Name,
    string OldCommand,
    ushort Category,
    string PayloadHex,
    string Description,
    string PayloadHint,
    LegacyCommandConfidence Confidence,
    bool IsDangerous = false)
{
    public string DisplayTitle => $"{Group} · {Name}";
    public string CandidateText => $"{Category:X4}" + (string.IsNullOrWhiteSpace(PayloadHex) ? string.Empty : $"  {PayloadHex}");
    public string StatusText => Confidence switch
    {
        LegacyCommandConfidence.Confirmed => "已确认可用",
        LegacyCommandConfidence.Integrated => "已在这台设备上看到，但用途还要继续确认",
        LegacyCommandConfidence.ExistingFamily => "程序里已有相近功能",
        _ => "来自其他型号，尚未确认",
    };
    public string RiskText => IsDangerous ? "可能让设备断开、重置或重新配对" : "可直接测试";
}

/// <summary>
/// One entry per EC command number. Different values of the same command are described
/// inside the entry instead of being duplicated as separate rows in the command lab.
/// </summary>
public static class LegacyEdifierCommandCatalog
{
    public static IReadOnlyList<LegacyEdifierCommandPreset> All { get; } = new LegacyEdifierCommandPreset[]
    {
        P("电量", "读取左耳、右耳和充电盒电量", "F2", 0xF200, "",
            "读取左耳、右耳和充电盒各自的电量。这是程序主界面显示电量时使用的方式。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("电量", "读取设备总电量", "D0", 0xD000, "",
            "读取整台设备的总体电量。程序已经能分别显示左耳、右耳和充电盒，所以这里只保留给手动测试使用。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),

        P("设备信息", "读取固件版本", "C6", 0xC600, "",
            "读取耳机固件版本。例如收到 01 00 00 时，程序会显示为 1.0.0。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("设备信息", "读取设备地址", "C8", 0xC800, "",
            "读取耳机自身的蓝牙地址。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("设备信息", "读取设备名称", "C9", 0xC900, "",
            "读取耳机当前名称。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("设备信息", "修改设备名称", "CA + 名称", 0xCA00, "46 69 74 42 75 64 73",
            "修改耳机名称。这里填写的是名称对应的数据；普通“设备”页面可以直接输入文字，不需要自己转换。耳机返回 01 表示名称已接受。",
            "示例内容对应 FitBuds。平时建议直接在“设备”页面修改名称。", LegacyCommandConfidence.Confirmed, true),

        P("声音场景", "读取当前声音场景", "08", 0x0800, "",
            "读取当前声音场景。返回 00 表示音乐，01 表示游戏，02 表示空间音效。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("声音场景", "设置声音场景", "09 + 场景", 0x0900, "00",
            "设置声音场景。第 1 个值：00=音乐，01=游戏，02=空间音效。",
            "填写 00、01 或 02。", LegacyCommandConfidence.Confirmed),

        P("降噪", "读取当前降噪", "CC", 0xCC00, "",
            "读取当前降噪模式和环境音强度。模式：01=深度降噪，02=舒适降噪，03=宽频降噪，04=防风降噪，05=环境音，06=关闭降噪。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("降噪", "设置降噪", "C1 + 设置内容", 0xC100, "1B 01",
            "这台设备已经确认的写法以 1B 开头：1B 01=深度降噪，1B 02=舒适降噪，1B 03=宽频降噪，1B 04=防风降噪，1B 05 xx=环境音，1B 06=关闭降噪。环境音最后一个值 03 到 09 分别对应 -3 到 +3。其他漫步者型号还出现过更短的 01=普通、02=降噪、03=环境音写法，但这台设备是否接受这种旧写法不能仅靠“有回应”判断。",
            "常用：1B 01 / 1B 02 / 1B 03 / 1B 04 / 1B 05 06 / 1B 06。", LegacyCommandConfidence.Confirmed),

        P("音质", "读取当前音质", "D5", 0xD500, "",
            "读取当前音质。返回 00=经典，01=低音增强，02=清晰人声，03=高音增强，04=自定义。只有自定义模式会使用四段均衡器。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("音质", "设置音质", "C4 + 音质", 0xC400, "00",
            "设置音质。第 1 个值：00=经典，01=低音增强，02=清晰人声，03=高音增强，04=自定义。只有 04 自定义时四段均衡器才生效。",
            "填写 00、01、02、03 或 04。", LegacyCommandConfidence.Confirmed),

        P("均衡器", "读取四段均衡器", "43", 0x4300, "",
            "读取四段均衡器。四个频率范围分别是 20–200、201–2000、2001–4000、4001–8000 Hz；增益范围 -3 到 +3，每次 0.5；Q 值范围 0.7 到 2.0，每次 0.1。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("均衡器", "设置一段均衡器", "44 + 一段设置", 0x4400, "00 00 00 64 06 07",
            "设置一段均衡器。第 1 个值是第几段（00–03）；接下来的两个值表示频率；倒数第 2 个值表示增益，06=0 dB，每变化 1 相当于 0.5 dB；最后一个值表示 Q 值的 10 倍，例如 07=0.7、14=2.0。",
            "普通使用建议在“声音与触控”的均衡器里调整；这里适合核对数据。", LegacyCommandConfidence.Confirmed),

        P("提示音", "读取提示音开关", "86", 0x8600, "",
            "读取提示音是否开启。常见返回中 00=关闭，01=开启。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("提示音", "读取提示音音量", "05", 0x0500, "",
            "读取提示音音量。这台设备已经确认会直接返回一个音量值，例如 07 表示音量 7。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("提示音", "设置提示音音量", "06 + 音量", 0x0600, "07",
            "设置提示音音量。这台设备已经手动确认可以使用；普通界面现在也使用这个方式设置音量，并在设置后再次读取音量确认结果。",
            "填写一个音量值。普通界面目前使用 0 到 15，例如 07 表示音量 7。", LegacyCommandConfidence.Confirmed),
        P("提示音", "设置提示音开关", "87 + 设置内容", 0x8700, "04 01 01",
            "打开或关闭提示音。04 01 00=关闭，04 01 01=打开。提示音音量已经改用 06 指令设置。",
            "关闭：04 01 00；打开：04 01 01。", LegacyCommandConfidence.Confirmed),

        P("定时关闭", "读取剩余时间", "D3", 0xD300, "",
            "读取距离自动关闭还剩多少分钟。返回的两个值合起来表示分钟数；00 00 表示没有开启定时关闭。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("定时关闭", "设置关闭时间", "D1 + 分钟", 0xD100, "00 1E",
            "设置多少分钟后关闭设备。两个值合起来表示分钟数，例如 00 1E=30 分钟，00 3C=60 分钟。",
            "示例 00 1E 表示 30 分钟。普通界面会自动换算。", LegacyCommandConfidence.Confirmed),
        P("定时关闭", "关闭定时", "D2", 0xD200, "",
            "关闭已经设置的定时关闭。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),

        P("触控", "读取触控功能", "F0 + 动作", 0xF000, "01",
            "读取某一个触控动作当前设置的功能。动作：01=左耳双击，03=左耳三击，08=左耳长按，02=右耳双击，04=右耳三击，09=右耳长按。功能：01=无，02=播放/暂停，03=上一曲，04=下一曲，05=音量加，06=音量减，07=降噪模式轮换，08=声音场景选择，09=语音助手。其他型号还出现过 F0 0A 用于读取模式轮换组合，这台设备是否相同还没有确认。",
            "第 1 个值填动作编号：01 / 03 / 08 / 02 / 04 / 09。", LegacyCommandConfidence.Confirmed),
        P("触控", "设置触控功能", "F1 + 动作和功能", 0xF100, "01 02",
            "设置一个触控动作。第 1 个值选择动作：01=左耳双击，03=左耳三击，08=左耳长按，02=右耳双击，04=右耳三击，09=右耳长按。第 2 个值选择功能：01=无，02=播放/暂停，03=上一曲，04=下一曲，05=音量加，06=音量减，07=降噪模式轮换，08=声音场景选择，09=语音助手。其他型号还出现过 F1 0A xx 用于设置模式轮换组合。注意：这台设备即使收到不完整或不合适的 F1 数据也可能返回内容，所以“收到返回”本身不代表设置有效。",
            "常用格式是“动作编号 功能编号”，例如 02 02=右耳双击播放/暂停。", LegacyCommandConfidence.Confirmed),

        P("设备操作", "关机", "CE", 0xCE00, "",
            "让耳机关机。这台设备不会返回确认；内容成功发出后就视为完成，随后耳机会离线。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed, true),
        P("设备操作", "断开连接", "CD", 0xCD00, "",
            "让耳机主动断开当前连接。这台设备不会返回确认；内容成功发出后就视为完成。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed, true),
        P("设备操作", "进入配对", "CF", 0xCF00, "",
            "让耳机进入配对状态。这台设备不会返回确认；内容成功发出后就视为完成。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed, true),
        P("设备操作", "重置设备", "07", 0x0700, "",
            "让耳机执行重置。这台设备不会返回确认；内容成功发出后就视为完成。重置可能改变设备设置或配对状态。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed, true),

        P("待确认信息", "读取未知开关", "68", 0x6800, "",
            "这台设备会稳定返回 00。根据返回只有一个值的形式，它很像某个开关状态，可能是 00=关闭、01=开启，但具体功能还没有确认。可以优先对照测试“双设备连接”；如果没有变化，再依次测试 LDAC、空间音效、佩戴检测等开关。每次只改变一个设置后再读取，最容易判断对应关系。",
            "不用填写其他内容。", LegacyCommandConfidence.Integrated),
        P("待确认信息", "读取未知功能状态", "91", 0x9100, "",
            "这台设备会稳定返回 0C 00。目前推测它可能是两个状态值，也可能是把多个功能的支持情况合在一起表示。已经确认它不会随着普通音质模式变化。建议一次只改变一个设置，再比较这两个值有没有变化。",
            "不用填写其他内容。", LegacyCommandConfidence.Integrated),
        P("播放状态", "读取当前播放状态", "C3", 0xC300, "",
            "读取当前播放状态。这台设备已经手动确认：03=暂停，0D=播放。播放状态发生变化时，耳机也会主动发来 C3 状态，所以不一定总有对应的读取请求。",
            "不用填写其他内容。", LegacyCommandConfidence.Confirmed),
        P("设备信息", "读取设备型号与功能信息", "D8", 0xD800, "",
            "这台设备会固定返回 27 个值。结合本机日志和同系列设备的协议资料，它很可能用来告诉应用当前耳机型号以及支持哪些功能，可以理解为设备的“功能说明”。目前这 27 个值分别对应什么功能还没有逐项拆清，因此这里只作为参考。一次已验证的返回是：C3 01 01 02 01 01 01 01 00 01 01 01 00 35 0C 00 29 E0 00 50 04 20 01 00 00 00 02。",
            "不用填写其他内容。", LegacyCommandConfidence.Integrated),
        P("佩戴检测", "读取佩戴检测设置", "FB", 0xFB00, "",
            "根据同系列设备的协议资料，这条指令很可能用于读取佩戴检测设置，例如摘下耳机后暂停、重新戴上后恢复播放。FitBuds Turbo 已确认会稳定返回 01 00，但这两个值各自对应什么选项还没有确认。可以在官方应用里切换佩戴检测相关开关后再次读取做对照。",
            "不用填写其他内容。", LegacyCommandConfidence.Integrated),
        P("触控", "读取触控灵敏度", "FD", 0xFD00, "",
            "根据同系列设备的协议资料，这条指令很可能用于读取触控灵敏度。FitBuds Turbo 已经多次实际发送，但目前没有收到回应，可能是这个型号不支持这项设置，也可能只会在特定状态下提供。",
            "不用填写其他内容。", LegacyCommandConfidence.Integrated),

        // Commands found in mEDIFIER but not yet confirmed on this FitBuds Turbo.
        P("其他型号参考", "读取 LDAC 状态", "48", 0x4800, "",
            "其他漫步者耳机用它读取 LDAC 状态。参考返回：00=关闭，01=48 kHz，02=96 kHz。这台设备尚未确认。",
            "不用填写其他内容。", LegacyCommandConfidence.Candidate),
        P("其他型号参考", "设置 LDAC", "49 + 模式", 0x4900, "00",
            "其他漫步者耳机用它设置 LDAC。参考值：00=关闭，01=48 kHz，02=96 kHz。修改后可能让设备重新配对。",
            "填写 00、01 或 02。", LegacyCommandConfidence.Candidate, true),
        P("其他型号参考", "读取自动关机", "D7", 0xD700, "",
            "其他漫步者耳机用它读取自动关机开关。参考返回：00=关闭，01=开启。这台设备尚未确认。",
            "不用填写其他内容。", LegacyCommandConfidence.Candidate),
        P("其他型号参考", "设置自动关机", "D6 + 开关", 0xD600, "00",
            "其他漫步者耳机用它设置自动关机。参考值：00=关闭，01=开启。这台设备尚未确认。",
            "填写 00 或 01。", LegacyCommandConfidence.Candidate),
        P("播放控制", "播放或暂停", "C2 + 操作", 0xC200, "00",
            "这台设备已经确认 00=播放、01=暂停。其他漫步者型号还会使用 02=音量加、03=音量减、04=下一曲、05=上一曲，但这些值在这台设备上目前没有验证成功，因此不要把它们当成可用功能。",
            "填写 00 播放，或 01 暂停。", LegacyCommandConfidence.Confirmed),
    };

    private static LegacyEdifierCommandPreset P(
        string group,
        string name,
        string oldCommand,
        ushort category,
        string payloadHex,
        string description,
        string payloadHint,
        LegacyCommandConfidence confidence,
        bool dangerous = false)
        => new(group, name, oldCommand, category, payloadHex, description, payloadHint, confidence, dangerous);
}
