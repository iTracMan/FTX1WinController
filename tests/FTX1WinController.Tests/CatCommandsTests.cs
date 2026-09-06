using FTX1WinController.Cat;
using FTX1WinController.Models;

namespace FTX1WinController.Tests;

/// Regression tests for the hardware-confirmed FTX-1 CAT protocol quirks
/// ported from the Mac app — mirrors the Mac app's own approach of pinning
/// exactly these kinds of parsing/encoding edge cases with tests.
public class CatCommandsTests
{
    // MARK: - Mic EQ (PR1): manual says 1=OFF/2=ON; hardware proved the real
    // scheme is plain 0=off/1=on, with the parser defensively accepting "2" as off too.

    [Fact]
    public void MicEq_Set_UsesPlainZeroOneEncoding_NotManualsOneTwo()
    {
        Assert.Equal("PR11;", CatCommands.SetMicEq(true));
        Assert.Equal("PR10;", CatCommands.SetMicEq(false));
    }

    [Fact]
    public void MicEq_Parse_AcceptsBothZeroAndTwoAsOff()
    {
        Assert.True(CatCommands.ParseMicEq("PR11;"));
        Assert.False(CatCommands.ParseMicEq("PR10;"));
        Assert.False(CatCommands.ParseMicEq("PR12;"));
    }

    // MARK: - AGC (GT): Set is a 5-value range (0-4); the Answer is a
    // different 7-value range (0-6) where 4/5/6 all collapse to Auto.

    [Fact]
    public void Agc_Set_UsesFiveValueRange()
    {
        Assert.Equal("GT00;", CatCommands.SetAgc(AgcMode.Off));
        Assert.Equal("GT04;", CatCommands.SetAgc(AgcMode.Auto));
    }

    [Theory]
    [InlineData("GT04;", AgcMode.Auto)]
    [InlineData("GT05;", AgcMode.Auto)]
    [InlineData("GT06;", AgcMode.Auto)]
    [InlineData("GT00;", AgcMode.Off)]
    [InlineData("GT01;", AgcMode.Fast)]
    [InlineData("GT02;", AgcMode.Mid)]
    [InlineData("GT03;", AgcMode.Slow)]
    public void Agc_Parse_CollapsesSevenValueAnswerToFiveValueMode(string reply, AgcMode expected)
    {
        Assert.Equal(expected, CatCommands.ParseAgc(reply));
    }

    // MARK: - AMS: no CAT command exists; the only trace is MD's "0" sentinel reply.

    [Fact]
    public void RadioMode_DisplayNameForCode_AmsSentinelIsNotAParseFailure()
    {
        Assert.Equal("AMS (auto)", RadioModeInfo.DisplayNameForCode('0'));
    }

    [Fact]
    public void RadioMode_DisplayNameForCode_RoundTripsEveryRealCode()
    {
        foreach (var mode in RadioModeInfo.All)
        {
            Assert.Equal(mode.DisplayName(), RadioModeInfo.DisplayNameForCode(mode.CatCode()));
        }
    }

    [Fact]
    public void RadioMode_DisplayNameForCode_UnknownCodeIsLabelled()
    {
        Assert.Equal("Unknown (Z)", RadioModeInfo.DisplayNameForCode('Z'));
    }

    // MARK: - TXW (TS): a latching toggle per the UI, but plain 0/1 on the wire.

    [Fact]
    public void Txw_RoundTrips_PlainZeroOneEncoding()
    {
        Assert.Equal("TS1;", CatCommands.SetTxw(true));
        Assert.Equal("TS0;", CatCommands.SetTxw(false));
        Assert.True(CatCommands.ParseTxw("TS1;"));
        Assert.False(CatCommands.ParseTxw("TS0;"));
    }

    // MARK: - Irregular VOX / CW break-in delay tables (not a clean formula).

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(3, 150)]
    [InlineData(4, 200)]
    [InlineData(5, 250)]
    [InlineData(6, 300)]
    [InlineData(7, 400)]
    [InlineData(33, 3000)]
    public void VoxDelayMs_MatchesIrregularThenLinearTable(int index, int expectedMs)
    {
        Assert.Equal(expectedMs, CatCommands.VoxDelayMs(index));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(5, 250)]
    [InlineData(6, 300)]
    [InlineData(33, 3000)]
    public void CwBreakInDelayMs_MatchesIrregularThenLinearTable(int index, int expectedMs)
    {
        Assert.Equal(expectedMs, CatCommands.CwBreakInDelayMs(index));
    }

    // MARK: - Signed-value formatting helper (AF Treble/Mid/Bass via EX01).

    [Fact]
    public void ExAudio_Set_UsesSignPlusTwoDigitMagnitude()
    {
        Assert.Equal("EX010101+10;", CatCommands.SetExAudio(CatCommands.ExAudioModeCategory.Ssb, CatCommands.ExAudioParam.Treble, 10));
        Assert.Equal("EX010101-05;", CatCommands.SetExAudio(CatCommands.ExAudioModeCategory.Ssb, CatCommands.ExAudioParam.Treble, -5));
    }

    [Fact]
    public void ExAudio_Parse_RoundTripsSignedValue()
    {
        Assert.Equal(10, CatCommands.ParseExAudio("EX010101+10;"));
        Assert.Equal(-5, CatCommands.ParseExAudio("EX010101-05;"));
        Assert.Equal(0, CatCommands.ParseExAudio("EX010101+00;"));
    }

    // MARK: - Compound/discriminator commands: BP (notch on/off vs frequency).

    [Fact]
    public void ManualNotch_Parse_DiscriminatesOnOffFromFrequency()
    {
        Assert.True(CatCommands.ParseManualNotch("BP00001;", out var isFreq1, out var value1));
        Assert.False(isFreq1);
        Assert.Equal(1, value1);

        Assert.True(CatCommands.ParseManualNotch("BP01050;", out var isFreq2, out var value2));
        Assert.True(isFreq2);
        Assert.Equal(500, value2); // raw 050 * 10 Hz/step
    }

    // MARK: - Compound/discriminator commands: CN (CTCSS vs DCS index).

    [Fact]
    public void ToneNumber_Parse_DiscriminatesCtcssFromDcs()
    {
        Assert.True(CatCommands.ParseToneNumber("CN00012;", out var isDcs1, out var index1));
        Assert.False(isDcs1);
        Assert.Equal(12, index1);

        Assert.True(CatCommands.ParseToneNumber("CN01023;", out var isDcs2, out var index2));
        Assert.True(isDcs2);
        Assert.Equal(23, index2);
    }

    // MARK: - Compound/discriminator commands: CF (clarifier on/off vs frequency).

    [Fact]
    public void ClarifierState_Parse_ReadsOnOffAnswer()
    {
        var reply = CatCommands.SetClarifierState(rxOn: true, txOn: false);
        Assert.True(CatCommands.ParseClarifierState(reply, out var rx, out var tx));
        Assert.True(rx);
        Assert.False(tx);
    }

    [Fact]
    public void ClarifierState_Parse_RejectsFrequencyAnswerDiscriminator()
    {
        // P3 (index 4) == '1' marks this as the frequency answer, not on/off.
        Assert.False(CatCommands.ParseClarifierState("CF001+0250;", out _, out _));
    }

    [Fact]
    public void ClarifierFrequency_RoundTrips_SignedFourDigitOffset()
    {
        Assert.Equal("CF001+0250;", CatCommands.SetClarifierFrequency(250));
        Assert.Equal("CF001-0250;", CatCommands.SetClarifierFrequency(-250));
        Assert.Equal(250, CatCommands.ParseClarifierFrequency("CF001+0250;"));
        Assert.Equal(-250, CatCommands.ParseClarifierFrequency("CF001-0250;"));
    }

    // MARK: - "Ask, don't assume": Power Control echoes back which amp is fitted.

    [Fact]
    public void Power_Parse_ReturnsRadioReportedAmpId()
    {
        Assert.True(CatCommands.ParsePower("PC2100;", out var amp, out var watts));
        Assert.Equal(2, amp);
        Assert.Equal(100, watts);
    }

    // MARK: - LM (arms recording) vs PB (plays back) must stay distinct commands.

    [Fact]
    public void VoiceMessageChannel_And_MessagePlayback_AreDifferentCommands()
    {
        Assert.Equal("LM03;", CatCommands.SetVoiceMessageChannel(3));
        Assert.Equal("PB03;", CatCommands.SetMessagePlayback(3));
        Assert.NotEqual(CatCommands.SetVoiceMessageChannel(3), CatCommands.SetMessagePlayback(3));
    }

    // MARK: - Frequency range clamping and basic round-trip sanity.

    [Fact]
    public void Frequency_RoundTrips()
    {
        var command = CatCommands.SetFrequency(14_250_000);
        Assert.Equal("FA014250000;", command);
        Assert.Equal(14_250_000, CatCommands.ParseFrequency("FA014250000;"));
    }

    [Fact]
    public void Frequency_ClampsOutOfRangeValues()
    {
        Assert.Equal("FA000030000;", CatCommands.SetFrequency(0));
        Assert.Equal("FA470000000;", CatCommands.SetFrequency(999_999_999));
    }
}
