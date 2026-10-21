using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TestFeatures;

/// <summary>
/// One long-lived STA thread that owns the WPF <see cref="System.Windows.Application"/> for every UI fixture.
/// WPF allows a single Application per process and it belongs to the thread that created it, so fixtures
/// must run their UI work on this thread instead of each starting their own. Work runs synchronously from a
/// queue (no message pump): layout is driven explicitly with UpdateLayout, so nothing runs behind the test's back.
/// </summary>
internal static class WpfTestHost
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static bool _started;
    private static string _startupFailure;
    private static readonly object Gate = new();

    /// <summary>Starts the host on first use. Returns a skip reason, or null when UI fixtures can run.</summary>
    public static string EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
                return null;
            if (_startupFailure != null)
                return _startupFailure;
            if (!Environment.UserInteractive)
                return _startupFailure = "no interactive desktop available";

            using var ready = new ManualResetEventSlim(false);
            string failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // Load App.xaml's resource dictionaries (the generated Main normally does this).
                    var app = new AuraClean.App();
                    app.InitializeComponent();
                }
                catch (Exception ex)
                {
                    failure = $"WPF host failed to start: {ex.GetType().Name}: {ex.Message}";
                }
                finally
                {
                    ready.Set();
                }

                if (failure != null)
                    return;
                foreach (var work in Queue.GetConsumingEnumerable())
                    work();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "WpfTestHost";
            thread.Start();
            ready.Wait();
            if (failure != null)
                return _startupFailure = failure;
            _started = true;
            return null;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the host thread; rethrows its exception; TimeoutException if it hangs.</summary>
    public static void Invoke(Action work, TimeSpan timeout)
    {
        var skip = EnsureStarted();
        if (skip != null)
            throw new InvalidOperationException(skip);

        Exception error = null;
        using var done = new ManualResetEventSlim(false);
        Queue.Add(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeout))
            throw new TimeoutException();
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
