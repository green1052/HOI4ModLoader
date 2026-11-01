using HOI4ModLoader;

namespace Signatures.Tests;

public unsafe class AirBaseCrashTests
{
    [Fact]
    public void MissingViewingCountryRecordIsSkipped()
    {
        // 043751 minidump: SAF tag/index 60, valid CAirBase, null country record.
        // Vanilla GetPools adds 0xB8 to that null and the row builder dereferences it.
        var airbase = stackalloc byte[0x80];
        new Span<byte>(airbase, 0x80).Clear();
        var countries = stackalloc nint[440];
        new Span<nint>(countries, 440).Clear();
        *(nint*)(airbase + 0x30) = (nint)countries;
        *(int*)(airbase + 0x3C) = 440;
        countries[1] = 0x10000; // another country having access is not sufficient

        Assert.False(GameFixes.AirBaseHasCountryData((nint)airbase, 60));
        countries[60] = 0x20000;
        Assert.True(GameFixes.AirBaseHasCountryData((nint)airbase, 60));
    }

    [Fact]
    public void UnavailableBaseAndCountryIndexAreSkipped()
    {
        var airbase = stackalloc byte[0x80];
        new Span<byte>(airbase, 0x80).Clear();
        var countries = stackalloc nint[1];
        countries[0] = 0x10000;
        *(nint*)(airbase + 0x30) = (nint)countries;
        *(int*)(airbase + 0x3C) = 1;

        Assert.False(GameFixes.AirBaseHasCountryData(0, 0));
        Assert.False(GameFixes.AirBaseHasCountryData((nint)airbase, -1));
        Assert.False(GameFixes.AirBaseHasCountryData((nint)airbase, 1));
        *(nint*)(airbase + 0x30) = 0;
        Assert.False(GameFixes.AirBaseHasCountryData((nint)airbase, 0));
    }
}
