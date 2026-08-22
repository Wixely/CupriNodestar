using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Drives managed <c>async</c> work from the browser's event loop.
///
/// <para><b>Why this exists.</b> NativeAOT-LLVM wasm runs on the browser's single thread and ships no event-loop
/// integration: there is no synchronization context, and no timer thread behind <c>Task.Delay</c>. So an
/// <c>await</c> on the main thread does not yield — it blocks the very loop that would have completed it, and the
/// renderer dies. Measured, not theorised: with a real seed the tab crashed immediately after
/// <c>createDataChannel</c>, which is exactly where managed code returned into an awaiting loop.</para>
///
/// <para><b>Why not ASYNCIFY.</b> Emscripten's usual answer to this does not build here — Binaryen's Asyncify pass
/// hits an <c>UNREACHABLE</c> on this module, so <c>wasm-opt</c> fails outright.</para>
///
/// <para>The remaining option is the one CupriFace uses for the same reason: JavaScript drives, managed code is
/// pumped. <c>requestAnimationFrame</c> calls <see cref="Tick"/>, which drains continuations posted here. Nothing
/// blocks, so the browser stays live between them.</para>
/// </summary>
internal static class BrowserLoop
{
    private static readonly ConcurrentQueue<Action> Pending = new();

    /// <summary>
    /// Called from JavaScript once per frame. Drains what is queued <i>at entry</i> rather than looping until empty:
    /// a continuation that queues more work would otherwise starve the frame and reintroduce the original problem.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "cupri_tick")]
    public static void Tick()
    {
        var budget = Pending.Count;
        for (var i = 0; i < budget && Pending.TryDequeue(out var work); i++)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // A throwing continuation must not take the pump down with it, or one failure ends every future
                // frame and the page simply stops with no explanation.
                Console.WriteLine($"[cupri] continuation faulted: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Awaits the next frame. The replacement for <c>Task.Delay</c>, which has no timer to run on here.</summary>
    public static Task NextFrameAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending.Enqueue(() => completion.SetResult());
        return completion.Task;
    }

    /// <summary>
    /// Starts asynchronous work without blocking. <c>Main</c> must never await: NativeAOT would block the thread on
    /// the returned task, which is the failure this whole class exists to avoid.
    /// </summary>
    public static void Run(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cupri] {ex.GetType().Name}: {ex.Message}");
            }
        });
    }
}
