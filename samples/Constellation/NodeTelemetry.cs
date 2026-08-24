using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Constellation;

/// <summary>
/// What this node is actually doing, sampled once a second and published over the Auspice rite.
///
/// <para><b>Every number here is measured, not invented.</b> That is the point of the demo: a synthetic sine wave
/// would animate just as prettily and would prove nothing, whereas these move because of what the node and its
/// visitors are really doing — which means a stalled feed looks like stalled numbers rather than like a screensaver
/// that happens to keep going.</para>
///
/// <para><b>Two of them respond to you directly</b>, and those are the ones worth watching: <c>viewers</c> counts
/// concurrent Auspice emanations, so opening a second tab makes it read 2; <c>oracleRequests</c> counts pages served
/// over L2, so navigating bumps it. Everything else is process-level truth from the runtime.</para>
///
/// <para><b>What is NOT here, and why.</b> Per-rite byte counters would be the obvious headline — bytes/sec split
/// across Oracle, Auspice and Conduit — and <c>CupriNode</c> exposes no such counters, so there is no honest way to
/// report them. The feed's own output is counted here instead, because that is a number this sample genuinely owns.
/// Inventing the rest and labelling it "traffic" would have been easy and would have been a lie.</para>
/// </summary>
internal sealed class NodeTelemetry
{
    /// <summary>How many samples a sparkline carries. Also the width of the chart in bars.</summary>
    private const int HistoryLength = 32;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Lock _gate = new();

    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _allocHistory = new();
    private readonly Queue<double> _feedHistory = new();

    private TimeSpan _lastCpu;
    private DateTimeOffset _lastSample = DateTimeOffset.UtcNow;
    private long _lastAllocated;
    private long _lastBytesPushed;

    // Shared across every attending session, because one node has one set of these however many people are watching.
    private int _viewers;
    private long _oracleRequests;
    private long _updatesPushed;
    private long _bytesPushed;

    /// <summary>Concurrent Auspice emanations — one per attending browser. The number that moves when you open a tab.</summary>
    public IDisposable EnterViewer()
    {
        Interlocked.Increment(ref _viewers);
        return new Leaver(this);
    }

    /// <summary>Counted by the wrapper around the site handler in <c>Program</c>, so navigating is visible.</summary>
    public void CountOracleRequest() => Interlocked.Increment(ref _oracleRequests);

    /// <summary>This feed's own output, which is the only traffic figure this sample can honestly claim.</summary>
    public void CountPush(int bytes)
    {
        Interlocked.Increment(ref _updatesPushed);
        Interlocked.Add(ref _bytesPushed, bytes);
    }

    /// <summary>
    /// Samples the runtime and rolls the sparklines forward one step.
    ///
    /// <para>Called once per publisher tick and guarded, because every attending session runs its own emanation loop:
    /// without the lock, two viewers would each advance the history and the chart would scroll at double speed for
    /// both. The <i>rates</i> are computed against a shared last-sample time for the same reason.</para>
    /// </summary>
    public JsonObject Sample()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastSample).TotalSeconds;
            if (elapsed <= 0) elapsed = 1;

            _process.Refresh();
            var cpuTime = _process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var pushed = Interlocked.Read(ref _bytesPushed);

            // Percentage of ONE core, so a busy node can legitimately read over 100 on a multi-core machine. Clamped
            // only at the bottom: a negative would mean the clock went backwards, not that the node idled.
            var cpuPercent = Math.Max(0, (cpuTime - _lastCpu).TotalSeconds / elapsed * 100);
            var allocRate = Math.Max(0, (allocated - _lastAllocated) / elapsed / 1024);      // KiB/s
            var feedRate = Math.Max(0, (pushed - _lastBytesPushed) / elapsed);               // B/s

            _lastCpu = cpuTime;
            _lastAllocated = allocated;
            _lastBytesPushed = pushed;
            _lastSample = now;

            Roll(_cpuHistory, cpuPercent);
            Roll(_allocHistory, allocRate);
            Roll(_feedHistory, feedRate);

            return new JsonObject
            {
                ["live"] = new JsonObject
                {
                    ["viewers"] = Volatile.Read(ref _viewers),
                    ["oracleRequests"] = Interlocked.Read(ref _oracleRequests),
                    ["updatesPushed"] = Interlocked.Read(ref _updatesPushed),
                    ["bytesPushed"] = pushed,
                },
                ["runtime"] = new JsonObject
                {
                    ["uptime"] = Format(now - _started),
                    ["cpuPercent"] = Math.Round(cpuPercent, 1),
                    ["workingSetMb"] = Math.Round(_process.WorkingSet64 / 1024d / 1024d, 1),
                    ["allocatedMb"] = Math.Round(allocated / 1024d / 1024d, 1),
                    ["threads"] = _process.Threads.Count,
                    ["gen0"] = GC.CollectionCount(0),
                },
                ["charts"] = new JsonArray(
                    Chart("CPU", "% of one core", _cpuHistory, cpuPercent, "0.0"),
                    Chart("Allocations", "KiB/s", _allocHistory, allocRate, "0"),
                    Chart("Feed output", "B/s", _feedHistory, feedRate, "0")),
            };
        }
    }

    private static void Roll(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
    }

    /// <summary>
    /// One sparkline, pre-scaled for the view.
    ///
    /// <para>The bars carry a ready-made <c>scale</c> of 0–1. The markup cannot compute one — there is no script
    /// engine on the other end — so the node does the arithmetic and sends the result.</para>
    ///
    /// <para><b>Why a scale and not a height.</b> CupriFace tweens <c>transform</c> and does not tween layout
    /// properties, so a scaled bar GLIDES to its new value between feed messages where a bound height would snap.
    /// This was a bound height until CupriFace 0.2.12: before that the engine ignored <c>transform-origin</c> and
    /// scaled about the element's centre, turning a rising series into a symmetric bowtie
    /// (<see href="https://github.com/Wixely/CupriFace/issues/54">CupriFace#54</see>).</para>
    ///
    /// <para>Scaled against the window's own peak, so a flat-but-nonzero series still reads as a line rather than as
    /// nothing. The peak is published so the axis can say what "full height" currently means.</para>
    /// </summary>
    private static JsonObject Chart(string label, string unit, Queue<double> history, double current, string format)
    {
        var samples = history.ToArray();
        var peak = samples.Length == 0 ? 0 : samples.Max();
        var bars = new JsonArray();

        // Left-pad so a young series grows in from the right rather than stretching across the whole axis.
        for (var i = 0; i < HistoryLength - samples.Length; i++)
            bars.Add(new JsonObject { ["scale"] = 0.02, ["idle"] = true });

        foreach (var sample in samples)
        {
            // A visible floor: a real zero should still show a baseline tick, or the chart looks broken rather than quiet.
            var fraction = peak <= 0 ? 0.02 : Math.Max(0.02, sample / peak);
            bars.Add(new JsonObject { ["scale"] = Math.Round(fraction, 4), ["idle"] = false });
        }

        return new JsonObject
        {
            ["label"] = label,
            ["unit"] = unit,
            ["current"] = current.ToString(format, System.Globalization.CultureInfo.InvariantCulture),
            ["peak"] = peak.ToString(format, System.Globalization.CultureInfo.InvariantCulture),
            ["bars"] = bars,
        };
    }

    private static string Format(TimeSpan uptime) => uptime.TotalHours >= 1
        ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
        : uptime.TotalMinutes >= 1 ? $"{uptime.Minutes}m {uptime.Seconds}s" : $"{uptime.Seconds}s";

    private sealed class Leaver(NodeTelemetry owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            // Guarded: a double dispose would drive the viewer count negative, and a demo that reads "-1 watching"
            // is worse than one that reads nothing.
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._viewers);
        }
    }
}
