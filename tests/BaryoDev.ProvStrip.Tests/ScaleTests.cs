using System.Diagnostics;
using System.Text;
using BaryoDev.ProvStrip.Engine;

namespace BaryoDev.ProvStrip.Tests;

// Work must grow with the file, not with its square. Each test is sized so a quadratic pass takes
// minutes and a linear one well under a second, and fails on a time bound instead of hanging.
public class ScaleTests
{
    [Fact]
    public async Task LongMpfTrailerIsReadInLinearTime()
    {
        var clean = Fixtures.Read("clean.jpg");
        var primary = Fixtures.Read("mpf-trailer.jpg")[..^clean.Length];
        const int images = 20_000;
        var file = new byte[primary.Length + clean.Length * images];
        primary.CopyTo(file, 0);
        for (var i = 0; i < images; i++) clean.CopyTo(file, primary.Length + i * clean.Length);

        var run = Task.Run(() => ProvStripEngine.Inspect(file));
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))) == run).ShouldBeTrue($"a trailer of {images} images must be read in linear time");
        (await run).IsClean.ShouldBeTrue();
    }

    [Fact]
    public async Task ManyFindingsStripInLinearTime()
    {
        const int elements = 30_000;
        var svg = new StringBuilder("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape">""");
        for (var i = 0; i < elements; i++) svg.Append("""<rect inkscape:label="r" width="1" height="1"/>""");
        svg.Append("</svg>");
        var bytes = Encoding.UTF8.GetBytes(svg.ToString());

        var clock = Stopwatch.StartNew();
        var inspection = ProvStripEngine.Inspect(bytes);
        var inspect = clock.Elapsed;
        inspection.Findings.Count.ShouldBeGreaterThanOrEqualTo(elements);
        var decision = new StripPolicy(CategoryMode.Auto, CategoryMode.Auto).Decide(inspection);
        decision.Blocked.ShouldBeFalse();

        // Strip inspects twice itself, so a few inspections' worth of time is linear.
        var run = Task.Run(() => ProvStripEngine.Strip(bytes, decision.Remove));
        (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5) + inspect * 6)) == run).ShouldBeTrue($"stripping {decision.Remove.Count} findings must take linear time (one inspect took {inspect})");
        (await run).Remaining.ShouldBeEmpty();
    }
}
