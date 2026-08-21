using System.Text.Json;
using CAP.Avalonia.Services.Localization;
using Shouldly;

namespace UnitTests.Services.Localization;

/// <summary>
/// Guards the curated examples manifest (<c>examples/examples.json</c>): every
/// description key must exist in all five shipped languages — otherwise a raw
/// key leaks onto the Home screen — and every curated entry must point at a
/// .lun file that actually ships, since a typo would silently drop the example
/// from the learning path.
/// </summary>
public class ExamplesManifestLocalizationTests
{
    private sealed record ManifestEntry(string File, int Rank, string? DescriptionKey);

    [Fact]
    public void EveryManifestDescriptionKey_ExistsInEveryShippedLanguage()
    {
        var keys = ReadManifestEntries()
            .Select(entry => entry.DescriptionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();

        keys.ShouldNotBeEmpty("every rung of the curated ladder must carry a description key");

        foreach (var language in SupportedLanguage.All)
        {
            var table = LocalizationResourceLoader.Load(language.Code);
            foreach (var key in keys)
            {
                table.ContainsKey(key!).ShouldBeTrue($"description key '{key}' missing in strings-{language.Code}.json");
            }
        }
    }

    [Fact]
    public void EveryManifestEntry_PointsAtAShippedLunFile_WithUniqueRank()
    {
        var examplesDirectory = Path.Combine(FindRepoRoot(), "examples");
        var entries = ReadManifestEntries();

        foreach (var entry in entries)
        {
            System.IO.File.Exists(Path.Combine(examplesDirectory, entry.File))
                .ShouldBeTrue($"manifest file '{entry.File}' does not exist in examples/");
        }

        entries.Select(entry => entry.Rank).Distinct().Count()
            .ShouldBe(entries.Count, "manifest ranks must be unique to keep the ladder order deterministic");
    }

    [Fact]
    public void CuratedLadder_ListsTheSrLatch_AfterTheFourBitAdder()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var adderIndex = ladder.FindIndex(example => example.Name == "Logic Gate 4-Bit Adder");
        adderIndex.ShouldBeGreaterThanOrEqualTo(0, "the 4-bit adder rung must stay curated");

        var latchIndex = ladder.FindIndex(example => example.Name == "Logic Gate SR-Latch");
        latchIndex.ShouldBeGreaterThan(adderIndex,
            "the SR latch is the first sequential rung — it follows the datapath rungs");
        ladder[latchIndex].Level.ShouldBe("Sequential");
        ladder[latchIndex].DescriptionKey.ShouldBe("Examples.SrLatch.Description");
    }

    [Fact]
    public void CuratedLadder_ListsTheBit_BetweenTheSrLatchAndTheCounter()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var latchIndex = ladder.FindIndex(example => example.Name == "Logic Gate SR-Latch");
        latchIndex.ShouldBeGreaterThanOrEqualTo(0, "the SR latch rung must stay curated");

        var bitIndex = ladder.FindIndex(example => example.Name == "Logic Gate Bit");
        bitIndex.ShouldBeGreaterThan(latchIndex,
            "the load-enabled bit is the NAND2TETRIS Bit — it slots between the SR latch and the counter");
        ladder[bitIndex].Level.ShouldBe("Sequential");
        ladder[bitIndex].DescriptionKey.ShouldBe("Examples.Bit.Description");

        var counterIndex = ladder.FindIndex(example => example.Name == "Logic Gate Counter 2-bit");
        counterIndex.ShouldBeGreaterThan(bitIndex,
            "the 2-bit counter builds on the register bit — it stays the rung after the Bit");
    }

    [Fact]
    public void CuratedLadder_ListsTheRegister2Bit_BetweenTheBitAndTheCounter()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var bitIndex = ladder.FindIndex(example => example.Name == "Logic Gate Bit");
        bitIndex.ShouldBeGreaterThanOrEqualTo(0, "the load-enabled bit rung must stay curated");

        var registerIndex = ladder.FindIndex(example => example.Name == "Logic Gate Register 2-bit");
        registerIndex.ShouldBeGreaterThan(bitIndex,
            "the 2-bit register is the NAND2TETRIS Register — it slots between the Bit and the counter");
        ladder[registerIndex].Level.ShouldBe("Sequential");
        ladder[registerIndex].DescriptionKey.ShouldBe("Examples.Register2bit.Description");

        var counterIndex = ladder.FindIndex(example => example.Name == "Logic Gate Counter 2-bit");
        counterIndex.ShouldBeGreaterThan(registerIndex,
            "the 2-bit counter builds on the multi-bit register — it stays the rung after the Register");
    }

    [Fact]
    public void CuratedLadder_ListsTheRam2x2_BetweenTheRegisterAndTheCounter()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var registerIndex = ladder.FindIndex(example => example.Name == "Logic Gate Register 2-bit");
        registerIndex.ShouldBeGreaterThanOrEqualTo(0, "the 2-bit register rung must stay curated");

        var ramIndex = ladder.FindIndex(example => example.Name == "Logic Gate RAM 2x2");
        ramIndex.ShouldBeGreaterThan(registerIndex,
            "the 2x2 RAM is the NAND2TETRIS RAM slice — it slots between the Register and the counter");
        ladder[ramIndex].Level.ShouldBe("Sequential");
        ladder[ramIndex].DescriptionKey.ShouldBe("Examples.Ram2x2.Description");

        var counterIndex = ladder.FindIndex(example => example.Name == "Logic Gate Counter 2-bit");
        counterIndex.ShouldBeGreaterThan(ramIndex,
            "the 2-bit counter builds on addressable memory — it stays the rung after the RAM");
    }

    [Fact]
    public void CuratedLadder_ListsTheCounter2Bit_AfterTheSrLatch()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var latchIndex = ladder.FindIndex(example => example.Name == "Logic Gate SR-Latch");
        latchIndex.ShouldBeGreaterThanOrEqualTo(0, "the SR latch rung must stay curated");

        var counterIndex = ladder.FindIndex(example => example.Name == "Logic Gate Counter 2-bit");
        counterIndex.ShouldBeGreaterThan(latchIndex,
            "the 2-bit counter is the sequential rung after the latch — " +
            "the datapath stone that makes the circuit run");
        ladder[counterIndex].Level.ShouldBe("Sequential");
        ladder[counterIndex].DescriptionKey.ShouldBe("Examples.Counter2bit.Description");
    }

    [Fact]
    public void CuratedLadder_ListsThePc2Bit_AfterTheCounter()
    {
        var ladder = new CAP.Avalonia.Services.ExampleDesignsService().GetExamples()
            .Where(example => example.DescriptionKey != null)
            .ToList();

        var bitIndex = ladder.FindIndex(example => example.Name == "Logic Gate Bit");
        bitIndex.ShouldBeGreaterThanOrEqualTo(0, "the load-enabled bit rung must stay curated");

        var counterIndex = ladder.FindIndex(example => example.Name == "Logic Gate Counter 2-bit");
        counterIndex.ShouldBeGreaterThan(bitIndex,
            "the 2-bit counter builds on the register bit — it stays the rung after the Bit");

        var pcIndex = ladder.FindIndex(example => example.Name == "Logic Gate PC 2-bit");
        pcIndex.ShouldBeGreaterThan(counterIndex,
            "the 2-bit program counter composes the counter with the load-enabled Bit — " +
            "the NAND2TETRIS PC slice is the rung after the counter");
        ladder[pcIndex].Level.ShouldBe("Datapath");
        ladder[pcIndex].DescriptionKey.ShouldBe("Examples.Pc2bit.Description");
    }

    private static List<ManifestEntry> ReadManifestEntries()
    {
        var manifestPath = Path.Combine(FindRepoRoot(), "examples", "examples.json");
        System.IO.File.Exists(manifestPath)
            .ShouldBeTrue("examples/examples.json must ship with the examples folder");

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("examples").EnumerateArray()
            .Select(entry => new ManifestEntry(
                entry.GetProperty("file").GetString()!,
                entry.GetProperty("rank").GetInt32(),
                entry.TryGetProperty("descriptionKey", out var key) ? key.GetString() : null))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || System.IO.File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
