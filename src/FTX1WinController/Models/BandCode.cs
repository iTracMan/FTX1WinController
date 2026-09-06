namespace FTX1WinController.Models;

/// Ported 1:1 from the Mac app's CATProtocolV2.swift `BandCode` enum (BS
/// command). BS is Set-only — no Read/Answer exists for it at all, per the
/// manual and confirmed on hardware — so there is no way to ask the radio
/// which band it's on; the currently-selected band is inferred from the
/// last-known frequency instead, via FromFrequency below, ported from the
/// same hardware-confirmed IARU-edge table as RadioControllerV2.currentBandCode
/// in the Mac app.
public enum BandCode
{
    Mhz1_8, Mhz3_5, Mhz5, Mhz7,
    Mhz10, Mhz14, Mhz18, Mhz21,
    Mhz24_5, Mhz28, Mhz50, Mhz70Gen,
    Air, Mhz144, Mhz430,
}

public static class BandCodeInfo
{
    public static readonly BandCode[] All = (BandCode[])Enum.GetValues(typeof(BandCode));

    public static string Code(this BandCode band) => band switch
    {
        BandCode.Mhz1_8 => "00",
        BandCode.Mhz3_5 => "01",
        BandCode.Mhz5 => "02",
        BandCode.Mhz7 => "03",
        BandCode.Mhz10 => "04",
        BandCode.Mhz14 => "05",
        BandCode.Mhz18 => "06",
        BandCode.Mhz21 => "07",
        BandCode.Mhz24_5 => "08",
        BandCode.Mhz28 => "09",
        BandCode.Mhz50 => "10",
        BandCode.Mhz70Gen => "11",
        BandCode.Air => "12",
        BandCode.Mhz144 => "13",
        BandCode.Mhz430 => "14",
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    public static string DisplayName(this BandCode band) => band switch
    {
        BandCode.Mhz1_8 => "1.8",
        BandCode.Mhz3_5 => "3.5",
        BandCode.Mhz5 => "5.0",
        BandCode.Mhz7 => "7.0",
        BandCode.Mhz10 => "10",
        BandCode.Mhz14 => "14",
        BandCode.Mhz18 => "18",
        BandCode.Mhz21 => "21",
        BandCode.Mhz24_5 => "24.5",
        BandCode.Mhz28 => "28/29",
        BandCode.Mhz50 => "50",
        BandCode.Mhz70Gen => "70/GEN",
        BandCode.Air => "AIR",
        BandCode.Mhz144 => "144",
        BandCode.Mhz430 => "430",
        _ => "?",
    };

    /// Ported 1:1 from RadioControllerV2.swift's `currentBandCode` (Mac
    /// app) — same hardware-confirmed IARU Region 1 band edges (2026-09-01
    /// confirmed correct against the real BAND popup by watching it while
    /// tuning across the transitions).
    public static BandCode? FromFrequency(long hz)
    {
        double mhz = hz / 1_000_000.0;
        return mhz switch
        {
            >= 1.8 and < 2.0 => BandCode.Mhz1_8,
            >= 3.5 and < 4.0 => BandCode.Mhz3_5,
            >= 5.0 and < 5.5 => BandCode.Mhz5,
            >= 7.0 and < 7.3 => BandCode.Mhz7,
            >= 10.0 and < 10.15 => BandCode.Mhz10,
            >= 14.0 and < 14.35 => BandCode.Mhz14,
            >= 18.068 and < 18.168 => BandCode.Mhz18,
            >= 21.0 and < 21.45 => BandCode.Mhz21,
            >= 24.89 and < 24.99 => BandCode.Mhz24_5,
            >= 28.0 and < 29.7 => BandCode.Mhz28,
            >= 50.0 and < 54.0 => BandCode.Mhz50,
            >= 108.0 and < 137.0 => BandCode.Air,
            >= 144.0 and < 148.0 => BandCode.Mhz144,
            >= 430.0 and < 450.0 => BandCode.Mhz430,
            >= 54.0 and < 108.0 => BandCode.Mhz70Gen,
            >= 137.0 and < 144.0 => BandCode.Mhz70Gen,
            >= 148.0 and < 430.0 => BandCode.Mhz70Gen,
            _ => null,
        };
    }
}
