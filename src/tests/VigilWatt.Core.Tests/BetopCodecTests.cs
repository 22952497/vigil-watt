using VigilWatt.Core.Plugins.Betop;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// BetopCodec —— 北通 (BETOP) 手柄状态报告（report 0x02 / header 0x15）帧编解码。
/// 用例使用开源实现 oitsukiii/betop-battery 记录的鲲鹏20 真机原始帧
/// （见其 docs/protocol.md §4：`02 15 64 00 51 …` = 100% 充电、`02 15 5D 00 50 …` = 93% 放电）。
/// 该协议无校验和字段，因此这里只校验帧头与字段语义。
/// </summary>
public class BetopCodecTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>充电中（官方实测帧）：100% + 充电标志低 4 位 = 1。</summary>
    private static readonly byte[] ChargingFrame =
        Hex("02 15 64 00 51 01 01 00 00 00 00 00 00 00 00 00");

    /// <summary>用电池（官方实测帧）：0x5D = 93%，充电标志低 4 位 = 0。</summary>
    private static readonly byte[] DischargingFrame =
        Hex("02 15 5D 00 50 01 01 00 00 00 00 00 00 00 00 00");

    [Fact]
    public void C1_Header打包与拆包()
    {
        Assert.Equal((byte)0x15, BetopCodec.EncodeHeader(BetopCodec.CmdStatus, BetopCodec.SubStatus));
        var (cmd, subcmd) = BetopCodec.DecodeHeader(BetopCodec.StatusHeader);
        Assert.Equal(BetopCodec.CmdStatus, cmd);
        Assert.Equal(BetopCodec.SubStatus, subcmd);
    }

    [Fact]
    public void C2_构造查询帧与官方一致()
    {
        byte[] built = BetopCodec.BuildQuery(16);
        Assert.Equal(16, built.Length);
        Assert.Equal("0215" + new string('0', 28), Convert.ToHexString(built));
    }

    [Fact]
    public void C3_构造查询帧长度不足2应报错()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BetopCodec.BuildQuery(1));
    }

    [Fact]
    public void C4_解析充电帧_100且充电中()
    {
        var parsed = BetopCodec.ParseStatus(ChargingFrame);
        Assert.NotNull(parsed);
        Assert.Equal(100, parsed!.Value.Percent);
        Assert.True(parsed.Value.Charging);
    }

    [Fact]
    public void C5_解析放电帧_93且未充电()
    {
        var parsed = BetopCodec.ParseStatus(DischargingFrame);
        Assert.NotNull(parsed);
        Assert.Equal(93, parsed!.Value.Percent);
        Assert.False(parsed.Value.Charging);
    }

    [Fact]
    public void C6_非状态报告帧不误判()
    {
        // header 0x25 = 按键/摇杆报告，不是电量
        Assert.Null(BetopCodec.ParseStatus(Hex("02 25 42 00 01 00 00 00")));
        // report_id 不对
        Assert.Null(BetopCodec.ParseStatus(Hex("13 15 42 00 01 00 00 00")));
    }

    [Fact]
    public void C7_电量越界或占位值不采用()
    {
        Assert.Null(BetopCodec.ParseStatus(Hex("02 15 00 00 50 00 00 00")));   // 0 = 未就绪/占位
        Assert.Null(BetopCodec.ParseStatus(Hex("02 15 65 00 50 00 00 00")));   // 0x65=101 越界
    }

    [Fact]
    public void C8_长度不足不崩溃()
    {
        Assert.Null(BetopCodec.ParseStatus(Hex("02 15")));
        Assert.Null(BetopCodec.ParseStatus(Array.Empty<byte>()));
    }

    [Fact]
    public void C9_兼容无reportId前缀布局_整体右移一字节()
    {
        byte[] shifted = new byte[DischargingFrame.Length + 1];
        Array.Copy(DischargingFrame, 0, shifted, 1, DischargingFrame.Length);

        var parsed = BetopCodec.ParseStatus(shifted);
        Assert.NotNull(parsed);
        Assert.Equal(93, parsed!.Value.Percent);
        Assert.False(parsed.Value.Charging);
    }

    [Fact]
    public void C10_充电标志只看低4位()
    {
        // byte[4]=0xF0 → 低 4 位为 0 = 未充电（高 4 位是保留位，不能误判成充电）
        Assert.False(BetopCodec.ParseStatus(Hex("02 15 5D 00 F0 00 00 00"))!.Value.Charging);
        // byte[4]=0x0F → 低 4 位非 0 = 充电
        Assert.True(BetopCodec.ParseStatus(Hex("02 15 5D 00 0F 00 00 00"))!.Value.Charging);
    }

    // ---------------------------------------------------------------- T9 被动状态流
    // 真机帧（2026-10-06，USB\VID_20BC&PID_5073 的 Col04，dongle 持续推、不响应查询）。

    /// <summary>手柄唤醒后的状态流帧：byte23 = 0x58 = 88 → 73%。</summary>
    private static readonly byte[] T9StreamWake =
        Hex("04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 58");

    /// <summary>手柄休眠时 dongle 报的占位帧：byte23 = 0x50 = 80 → 67%。</summary>
    private static readonly byte[] T9StreamIdle =
        Hex("04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 50");

    /// <summary>充满后的状态流帧：byte23 = 0x78 = 120 → 100%（北通助手满格绿色）。</summary>
    private static readonly byte[] T9StreamFull =
        Hex("04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 78");

    [Fact]
    public void T1_解析T9状态流_唤醒_73()
    {
        var parsed = BetopCodec.ParseT9Status(T9StreamWake);
        Assert.NotNull(parsed);
        Assert.Equal(73, parsed!.Value.Percent);
        Assert.False(parsed.Value.Charging);
    }

    [Fact]
    public void T2_解析T9占位帧_休眠_67()
    {
        Assert.Equal(67, BetopCodec.ParseT9Status(T9StreamIdle)!.Value.Percent);
    }

    [Fact]
    public void T5_解析T9满值帧_100()
    {
        Assert.Equal(100, BetopCodec.ParseT9Status(T9StreamFull)!.Value.Percent);
    }

    [Fact]
    public void T6_T9电量换算覆盖各级别()
    {
        // raw = 等级(0~15) × 8，满值 120 = 100%
        Assert.Equal(0, BetopCodec.T9ToPercent(0));
        Assert.Equal(50, BetopCodec.T9ToPercent(60));    // 0x3C
        Assert.Equal(67, BetopCodec.T9ToPercent(80));    // 0x50
        Assert.Equal(73, BetopCodec.T9ToPercent(88));    // 0x58
        Assert.Equal(100, BetopCodec.T9ToPercent(120));  // 0x78
    }

    [Fact]
    public void T3_T9解析不误判其它帧()
    {
        // Col05 的摇杆流：帧头是变化的 0x46~0x6F，不是 0x02
        Assert.Null(BetopCodec.ParseT9Status(
            Hex("05 60 fd e7 fe 1b 0f 27 00 d9 ff f0 ff 01 06 00 80 80 80 80 00 00 00 00")));
        // 5075 接收器的全零 64 字节帧
        Assert.Null(BetopCodec.ParseT9Status(new byte[64]));
        // 长度不足
        Assert.Null(BetopCodec.ParseT9Status(Hex("04 02 80 80 80")));
        Assert.Null(BetopCodec.ParseT9Status(Array.Empty<byte>()));
    }

    [Fact]
    public void T4_T9电量越界不采用()    {
        // 0x7F = 127 > 满值 120，越界
        Assert.Null(BetopCodec.ParseT9Status(
            Hex("04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 7F")));
        // 0 = 未就绪/占位
        Assert.Null(BetopCodec.ParseT9Status(
            Hex("04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));
    }

    // ---------------------------------------------------------------- 产品串收拾成型号名

    [Fact]
    public void P1_T9真机产品串收拾成型号()
    {
        // 真机 USB product string：带厂商前缀 + "For …" 用途说明
        Assert.Equal("T9", BetopCodec.CleanProductName("BEITONG T9 For Keyboard & Mouse"));
        Assert.Equal("T9", BetopCodec.CleanProductName("BETOP T9 For PC"));
        Assert.Equal("T9", BetopCodec.CleanProductName("北通 T9 For PC"));
    }

    [Fact]
    public void P2_没有尾巴时也去掉厂商前缀()
    {
        Assert.Equal("KP20EB", BetopCodec.CleanProductName("KP20EB"));
        Assert.Equal("KP20EB", BetopCodec.CleanProductName("BEITONG KP20EB"));
        Assert.Equal("BTP-KP20EB", BetopCodec.CleanProductName("BETOP-BTP-KP20EB"));
    }

    [Fact]
    public void P3_空产品串返回空串()
    {
        Assert.Equal("", BetopCodec.CleanProductName(null));
        Assert.Equal("", BetopCodec.CleanProductName(""));
        Assert.Equal("", BetopCodec.CleanProductName("BEITONG"));   // 只剩厂商名 → 收拾后为空
    }
}
