namespace VigilWatt.Core.Plugins.Betop;

/// <summary>
/// 北通 (BETOP) 手柄状态报告的**纯帧编解码**——不碰句柄、不写日志，便于单测
/// （见 src/tests/VigilWatt.Core.Tests/，用真机原始帧跑）。
///
/// 帧结构（收发同构）：<c>[report_id, header, payload…]</c>，其中 header 一个字节拆两个 4 位字段：
/// <code>
///   bit: 7 6 5 4 | 3 2 1 0
///        └ subcmd ┘└── cmd ─┘     header = (subcmd &lt;&lt; 4) | cmd
/// </code>
/// 电量走"状态报告"：<c>cmd = 0x5</c>、<c>subcmd = 0x1</c> → header <c>0x15</c>，report_id <c>0x02</c>。
/// <list type="bullet">
///   <item>查询帧：<c>02 15</c>（其余补零到接口输出长度）；</item>
///   <item>响应帧：<c>02 15 &lt;电量%&gt; &lt;保留&gt; &lt;充电标志(低4位)&gt; …</c>。</item>
/// </list>
/// 真机实测：<c>02 15 64 00 51 …</c> = 100% 充电中；<c>02 15 5D 00 50 …</c> = 93% 用电池。
/// 协议出处：对北通官方 Electron 客户端的互操作性分析（开源实现 oitsukiii/betop-battery，MIT）。
/// </summary>
internal static class BetopCodec
{
    /// <summary>状态报告的 report ID（鲲鹏20 为 0x02；其它型号可能不同，协议家族一致）。</summary>
    public const byte ReportId = 0x02;

    /// <summary>状态报告命令号（cmd）。</summary>
    public const byte CmdStatus = 0x05;

    /// <summary>状态报告子命令（subcmd）。</summary>
    public const byte SubStatus = 0x01;

    /// <summary>状态报告的 header 字节 = (subcmd &lt;&lt; 4) | cmd = 0x15。</summary>
    public const byte StatusHeader = (SubStatus << 4) | CmdStatus;

    /// <summary>整帧偏移：电量百分比（0~100）。</summary>
    public const int BatteryOffset = 2;

    /// <summary>整帧偏移：充电标志所在字节（取低 4 位）。</summary>
    public const int ChargeOffset = 4;

    /// <summary>充电标志掩码（低 4 位，非 0 = 充电中）。</summary>
    public const byte ChargeMask = 0x0F;

    /// <summary>无效电量下界：0 视为设备未就绪/占位值，不接受。</summary>
    public const byte InvalidPercentFloor = 0;

    // ---------------------------------------------------------------- T9 被动状态流
    // 鲲鹏20 走"查询 0x15 → 响应 0x15"；但 T9（USB\VID_20BC&PID_5073）**不响应查询**，
    // 而是由 dongle 在厂商接口 Col04 上**持续推**一帧状态流。真机实测：
    //   04 02 80 80 80 80 00 00 … 00 78     ← byte23 = 0x78(120) 充满，北通助手满格绿色
    //   04 02 80 80 80 80 00 00 … 00 58     ← byte23 = 0x58(88)  唤醒
    //   04 02 80 80 80 80 00 00 … 00 50     ← byte23 = 0x50(80)  手柄休眠时 dongle 报的占位值
    //
    // ⚠ 2026-10-06 踩坑（务必别再犯）：**byte[1] 不是固定的帧头**。当天上午是 0x02、下午变成 0x0C
    //   （`04 0C 80 80 80 80 … 58`），旧代码把 `byte[1] == 0x02` 当成识别特征 → 解析全数落空
    //   → 手柄从设备列表消失，而且每次轮询仍要白等 6 秒探测窗口。翻日志 81 条 Col04 帧：
    //   byte[1] ∈ {0x02, 0x0C}，byte[23] ∈ {80, 88}，其余字节完全一致 —— byte[1] 是**可变状态位**。
    //   识别特征只能取真正稳定的部分：**report ID 0x04（Col04 这条 collection）** + 帧长 ≥24。
    //   （Col05 的摇杆流 report ID 是 0x05、Col01 的配置流是 0x01，天然被 report ID 排除。）

    /// <summary>T9 状态流所在 collection 的 report ID（整帧偏移 0 处；无 ID 前缀的布局在偏移 1）。</summary>
    public const byte T9StreamReportId = 0x04;

    /// <summary>T9 状态流帧最小长度（含 report ID）。</summary>
    public const int T9MinLength = 24;

    /// <summary>T9 电量原始字节所在偏移。</summary>
    public const int T9BatteryOffset = 23;

    /// <summary>
    /// T9 电量满值。实测该字节只出现 8 的倍数（0x50=80 / 0x58=88 / 0x78=120），
    /// 是"4-bit 电量等级(0~15) × 8"的编码：15 × 8 = 120 = 100%。
    /// 对照依据：byte23 = 0x78(120) 时北通助手显示**满格绿色**。
    /// </summary>
    public const byte T9BatteryFull = 120;

    // ---------------------------------------------------------------- "手柄未连接"占位帧
    //
    // ⚠ 2026-10-06 晚（用户复报："我设备已经关闭了 只有连接器还在电脑上"）：
    //   **dongle 在手柄关机/休眠后不会停止推流**，而是继续每轮推同一帧，只是电量字节换成
    //   一个**固定的占位值** —— 旧实现照单全收，于是手柄明明关了，列表里还显示"北通 T9 67% 在线"。
    //
    // 真机对照（同一天、同一台 T9，翻 app.log 全部 Col04 帧）：
    //
    //   | 时刻              | 帧                                              | 现场          |
    //   |-------------------|-------------------------------------------------|---------------|
    //   | 16:38 ~ 17:02     | `04 0C 80 80 80 80 … 58`（byte23=0x58）          | 手柄开着 73%  |
    //   | 17:03 ~ 现在      | `04 04 80 80 80 80 … 50`（byte23=0x50）          | **手柄已关**  |
    //   | 15:13 ~ 15:18     | `04 02 … 58`                                     | 手柄开着      |
    //   | 15:19 ~ 15:30     | `04 02 … 50`                                     | 手柄休眠      |
    //
    //   结论：**byte23 == 0x50（raw 80）就是"手柄没连上"** —— 开机的两段都是 0x58，
    //   关机/休眠的两段都是 0x50，一次例外都没有；而 byte[1] 在 {0x02, 0x04, 0x0C} 之间乱变，
    //   不能当判据（关机时是 0x04、休眠时是 0x02、开机时是 0x02/0x0C，无规律）。
    //
    // 为什么只认 0x50 这一个值、而不是按"低位/标志位"去猜：
    //   0x50 与 0x58 只差最低位，因此有两种自洽读法——① 等级×8（0x50=等级10=67%）；
    //   ② 高 4 位是等级、低位 0x08 是"链路有效"标志（0x50=等级5+标志清零）。
    //   两种读法下 **0x50 都只可能是"没连上"**（开机态永远是 0x58），所以认 0x50 是零风险的选择；
    //   而按"低位=标志"去判，若实际是读法①，则所有偶数等级（0x40/0x60/0x70…）都会被误判离线——
    //   那是把一半正常读数打掉。**宁可漏判、不可误判**，故只认 0x50。
    //   留待验证：充满（0x78）后关机，若 dongle 报 0x70 则读法②成立，可据此把判据一般化。

    /// <summary>
    /// T9 状态流里 dongle 在"手柄未连接（关机/休眠/未配对）"时上报的**占位值**（raw 80）。
    /// 见上方对照表：开机态永远是 0x58，0x50 只在手柄没连上时出现。
    /// </summary>
    public const byte T9NotLinkedRaw = 0x50;

    /// <summary>把 T9 原始电量字节（0~120）换算成百分比（0~100，四舍五入）。</summary>
    public static int T9ToPercent(byte raw) => (raw * 100 + T9BatteryFull / 2) / T9BatteryFull;

    /// <summary>T9 被动状态流的解析结果。</summary>
    /// <param name="Percent">电量百分比（0~100）。<paramref name="Linked"/> 为 false 时该值无意义（是 dongle 的占位值）。</param>
    /// <param name="Charging">是否充电中（T9 流里没有充电标志字节，恒为 false）。</param>
    /// <param name="Linked">
    /// 手柄是否真的连着。false = dongle 上报"未连接"占位帧 —— **手柄并不在线**，
    /// 调用方应把设备标离线（不要把这个占位值当成电量）。
    /// </param>
    public readonly record struct T9Status(int Percent, bool Charging, bool Linked);

    /// <summary>
    /// 解析 T9 的被动状态流帧。返回 <see cref="T9Status"/>；非该帧型或电量越界返回 null。
    /// 识别依据只有 report ID（见上方踩坑记录），**不要**再按 byte[1] 判帧头。
    /// <see cref="T9Status.Linked"/> = false 表示 dongle 明确告知"手柄没连上"（见 <see cref="T9NotLinkedRaw"/>）。
    /// </summary>
    public static T9Status? ParseT9Status(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + T9MinLength) continue;
            if (input[off] != T9StreamReportId) continue;

            byte raw = input[off + T9BatteryOffset];
            if (raw <= InvalidPercentFloor || raw > T9BatteryFull) return null;

            // 占位帧：dongle 还插着、手柄没连上 —— 值不是电量，必须交给调用方判离线。
            if (raw == T9NotLinkedRaw) return new T9Status(0, false, Linked: false);

            return new T9Status(T9ToPercent(raw), false, Linked: true);
        }
        return null;
    }

    /// <summary>
    /// 把 Windows 的 USB product string 收拾成**型号名**。
    ///
    /// 真机：T9 的 product string 是 <c>"BEITONG T9 For Keyboard & Mouse"</c> —— 前面是厂商名、
    /// 后面 <c>"For …"</c> 是给系统看的用途说明，都不是型号。直接显示会变成
    /// "北通 BEITONG T9 For Keyboard & Mouse"（而且"Keyboard"还会让 UI 把它画成键盘图标）。
    /// 这里收拾成 <c>"T9"</c>。
    /// </summary>
    public static string CleanProductName(string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return "";
        var s = product.Trim();

        foreach (var vendor in new[] { "BEITONG", "BETOP", "北通" })
        {
            if (s.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            {
                s = s[vendor.Length..].TrimStart(' ', '-', '_', ':', '：');
                break;
            }
        }

        int forIdx = s.IndexOf(" For ", StringComparison.OrdinalIgnoreCase);
        if (forIdx > 0) s = s[..forIdx];

        return s.Trim();
    }

    /// <summary>把 cmd / subcmd 打包成一个 header 字节。</summary>
    public static byte EncodeHeader(byte cmd, byte subcmd)
        => (byte)(((subcmd & 0x0F) << 4) | (cmd & 0x0F));

    /// <summary>把 header 字节拆成 (cmd, subcmd)。</summary>
    public static (byte Cmd, byte Subcmd) DecodeHeader(byte header)
        => ((byte)(header & 0x0F), (byte)((header >> 4) & 0x0F));

    /// <summary>
    /// 构造一条查询帧（不足部分补零）。北通接收器对**写入长度**敏感——必须写满接口声明的
    /// 输出报文长度（HID 驱动要求），所以长度由调用方按接口给出。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 小于 2。</exception>
    public static byte[] BuildQuery(int length, byte reportId = ReportId, byte header = StatusHeader)
    {
        if (length < 2)
            throw new ArgumentOutOfRangeException(nameof(length), "长度至少 2（report_id + header）");
        var frame = new byte[length];
        frame[0] = reportId;
        frame[1] = header;
        return frame;
    }

    /// <summary>接口未声明 report ID（描述符无 0x85 项）时：header 直接放报文首字节，无 ID 前缀。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 小于 1。</exception>
    public static byte[] BuildQueryWithoutReportId(int length, byte header = StatusHeader)
    {
        if (length < 1)
            throw new ArgumentOutOfRangeException(nameof(length), "长度至少 1（header）");
        var frame = new byte[length];
        frame[0] = header;
        return frame;
    }

    /// <summary>
    /// 解析状态报告帧（header 0x15）。返回 (电量%, 是否充电中)；不是状态报告或电量无效时返回 null。
    /// Windows 的 ReadInput 缓冲带 report ID 前缀（下标 0），这里同时兼容无前缀布局（下标 1）。
    /// </summary>
    public static (int Percent, bool Charging)? ParseStatus(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + ChargeOffset + 1) continue;
            if (input[off] != ReportId || input[off + 1] != StatusHeader) continue;

            byte pct = input[off + BatteryOffset];
            if (pct <= InvalidPercentFloor || pct > 100) return null;   // 0/越界 = 占位值，不接受
            bool charging = (input[off + ChargeOffset] & ChargeMask) != 0;
            return (pct, charging);
        }
        return null;
    }
}
