using ShiftBridge.Core.Trainer;

namespace ShiftBridge.Core.Tests;

public class LatencyTrackerTests
{
    [Fact]
    public void Summary_reports_median_p95_and_count_per_label_then_resets()
    {
        var t = new LatencyTracker();
        for (int i = 1; i <= 100; i++) t.Add("natif", i);                 // 1..100 ms
        t.Add("FTMS", 140);

        var s = t.TakeSummary();
        Assert.NotNull(s);
        Assert.Contains("natif médiane 51 ms (p95 95, n=100)", s);        // médiane 50,5 → 51 ; p95 = 95,05 → 95
        Assert.Contains("FTMS médiane 140 ms (p95 140, n=1)", s);
        Assert.Null(t.TakeSummary());                                      // remis à zéro
    }

    [Fact]
    public void Window_keeps_only_the_latest_samples_and_ignores_invalid_ones()
    {
        var t = new LatencyTracker(window: 3);
        foreach (var v in new[] { 1000.0, 10, 20, 30 }) t.Add("x", v);
        t.Add("x", -5);
        t.Add("x", double.NaN);
        Assert.Contains("x médiane 20 ms (p95 29, n=3)", t.TakeSummary());
    }

    [Fact]
    public void No_samples_gives_no_summary() => Assert.Null(new LatencyTracker().TakeSummary());
}

public class BikeDataTests
{
    [Fact]
    public void Power_is_read_whatever_the_optional_fields_before_it()
    {
        // vitesse + cadence + puissance (flags 0x44) : puissance à l'octet 6
        Assert.True(FtmsCodec.TryGetPowerW([0x44, 0x00, 0x10, 0x0E, 0xA0, 0x00, 0xC8, 0x00], out var w1));
        Assert.Equal(200, w1);
        // + distance (0x10) et résistance (0x20) avant la puissance : 2 + 2 + 2 + 3 + 2 = 11
        Assert.True(FtmsCodec.TryGetPowerW([0x74, 0x00, 0x10, 0x0E, 0xA0, 0x00, 1, 2, 3, 4, 0, 0x2C, 0x01], out var w2));
        Assert.Equal(300, w2);
        Assert.False(FtmsCodec.TryGetPowerW([0x04, 0x00, 0x10, 0x0E, 0xA0, 0x00], out _));      // pas de puissance
        Assert.False(FtmsCodec.TryGetPowerW([0x44, 0x00, 0x10], out _));                          // trame tronquée
    }

    [Fact]
    public void Monitor_reports_count_longest_silence_and_last_power_then_resets()
    {
        var m = new BikeDataMonitor();
        Assert.Null(m.TakeSummary(0));
        m.Add(1000, 150); m.Add(1250, 160); m.Add(1300, 170);
        var s = m.TakeSummary(1400);
        Assert.Contains("3 reçues", s);
        Assert.DoesNotContain("relayées", s);                                                       // toutes relayées : rien à signaler
        Assert.Contains("plus long silence 250 ms", s);
        Assert.Contains("dernière puissance 170 W", s);
        Assert.Null(m.TakeSummary(1500));
    }

    [Fact]
    public void Monitor_stays_quiet_while_the_flow_is_healthy_and_reports_a_full_stall()
    {
        var m = new BikeDataMonitor();
        m.Add(0, 100); Assert.NotNull(m.TakeSummary(100));                                         // premier résumé : toujours
        for (long t = 500; t <= 30_000; t += 500) m.Add(t, 100);
        Assert.Null(m.TakeSummary(30_100));                                                         // flux régulier : silence
        m.Add(30_200, 100);
        Assert.NotNull(m.TakeSummary(30_300, verbose: true));                                       // journal détaillé : toujours

        var stall = m.TakeSummary(60_000);                                                          // plus rien depuis ~30 s
        Assert.Contains("Aucune mesure du trainer depuis", stall);
    }

    [Fact]
    public void Monitor_reports_measurements_not_relayed_to_the_app()
    {
        var m = new BikeDataMonitor();
        m.Add(0, 100, relayed: false); m.Add(500, 100, relayed: false);
        Assert.Contains("(0 relayées à l'appli)", m.TakeSummary(600));
    }

    [Fact]
    public void Monitor_counts_the_silence_since_the_last_sample_and_flags_missing_power()
    {
        var m = new BikeDataMonitor();
        m.Add(1000, null); m.Add(1100, null);
        var s = m.TakeSummary(4100);                                                              // 3 s sans aucune mesure
        Assert.Contains("plus long silence 3000 ms", s);
        Assert.Contains("pas de puissance", s);
    }
}

public class ArbiterTimingTests
{
    [Fact]
    public async Task Default_gap_is_adaptive_and_response_time_is_reported()
    {
        var sent = new List<byte[]>();
        using var arbiter = new ControlPointArbiter((cp, _) => { lock (sent) sent.Add(cp); return Task.CompletedTask; });
        Assert.InRange(arbiter.CurrentProxyGap.TotalMilliseconds, 80, 300);
        Assert.Equal(120, arbiter.CurrentProxyGap.TotalMilliseconds, 1);   // valeur de départ

        double? latency = null;
        arbiter.ResponseLatency += ms => latency = ms;
        arbiter.SubmitFromProxy([FtmsCodec.OpSetSimulation, 0, 0, 0, 0, 0, 0, 0, 0]);
        var end = Environment.TickCount64 + 2000;
        while (sent.Count == 0 && Environment.TickCount64 < end) await Task.Delay(5);
        await Task.Delay(60);
        arbiter.OnResponse([0x80, FtmsCodec.OpSetSimulation, 0x01]);

        Assert.NotNull(latency);
        Assert.InRange(latency!.Value, 40, 1000);
        Assert.True(arbiter.CurrentProxyGap.TotalMilliseconds < 120);       // réponse rapide : l'écart diminue
    }

    [Fact]
    public void An_explicit_gap_stays_fixed()
    {
        using var arbiter = new ControlPointArbiter((_, _) => Task.CompletedTask, proxyMinGap: TimeSpan.FromMilliseconds(250));
        Assert.Equal(250, arbiter.CurrentProxyGap.TotalMilliseconds);
    }
}
