// Copyright (c) 2025 HuiTeab.
// Licensed under the PolyForm Noncommercial License 1.0.0.
// See LICENSE in the repository root for details.

namespace eft_dma_radar.Silk.Misc
{
    /// <summary>
    /// Log severity level.
    /// </summary>
    public enum AppLogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Lightweight logger for the Silk radar. Console + optional file sink.
    /// Thread-safe.
    /// </summary>
    public static class Log
    {
        private static StreamWriter? _fileWriter;
        private static bool _consoleAllocated;
        private static readonly Lock _writeLock = new();
        private static ConsoleColor _currentColor = ConsoleColor.Gray;

        // Console/file output happens on a dedicated background thread; callers only enqueue.
        // Writing synchronously meant any thread that logged blocked on console I/O (and, with
        // -logging, a flushed file write) — a console is easy to stall (QuickEdit selection,
        // scrolling, a busy terminal). On a raid start that showed up as ~2 s of the first
        // registration pass (0.15–1 s gaps between "Discovered" and the very next log line,
        // with nothing but a log call in between). Bounded so a wedged console can't grow
        // memory without limit: when full, lines are dropped (and counted), never blocking.
        private const int LogQueueCapacity = 100_000;
        private static readonly BlockingCollection<(DateTime Utc, string Message)> _queue =
            new(new ConcurrentQueue<(DateTime, string)>(), LogQueueCapacity);
        private static long _droppedLines;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GetConsoleWindow();

        static Log()
        {
            AllocateConsole();

            string[] args = Environment.GetCommandLineArgs();
            if (args?.Contains("-logging", StringComparer.OrdinalIgnoreCase) ?? false)
            {
                string logFileName = $"log-{DateTime.UtcNow.ToFileTime()}.txt";
                var fs = new FileStream(logFileName, FileMode.Create, FileAccess.Write);
                // AutoFlush: closing the console window (or a hard crash) kills the process
                // without ProcessExit, and a 4 KB buffer then silently loses the very lines that
                // show where it stopped. -logging is a diagnostic mode; durability wins.
                _fileWriter = new StreamWriter(fs, Encoding.UTF8, 0x1000) { AutoFlush = true };
            }

            var writer = new Thread(WriterLoop)
            {
                Name = "LogWriter",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            writer.Start();

            // Drain what's still queued before the file is closed on a normal exit.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                _queue.CompleteAdding();
                writer.Join(TimeSpan.FromSeconds(2));
                var w = Interlocked.Exchange(ref _fileWriter, null);
                w?.Dispose();
            };
        }

        private static void WriterLoop()
        {
            foreach (var (utc, message) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    WriteCore(utc, message);

                    if (_queue.Count == 0)
                    {
                        long dropped = Interlocked.Exchange(ref _droppedLines, 0);
                        if (dropped > 0)
                            WriteCore(DateTime.UtcNow, $"WARNING [Log] Dropped {dropped} log line(s) — console/file output couldn't keep up.");
                    }
                }
                catch
                {
                    // Logging must never take the process down.
                }
            }
        }

        private static void Enqueue(string message)
        {
            try
            {
                if (!_queue.TryAdd((DateTime.UtcNow, message)))
                    Interlocked.Increment(ref _droppedLines);
            }
            catch (InvalidOperationException)
            {
                // Queue completed (process shutting down) — nothing left to write to.
            }
        }

        private static void AllocateConsole()
        {
            if (_consoleAllocated)
                return;
            try
            {
                if (GetConsoleWindow() == 0)
                {
                    if (AllocConsole())
                    {
                        Console.OutputEncoding = Encoding.UTF8;
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
                        Console.Title = "eft-dma-radar — Debug Console";
                        _consoleAllocated = true;
                    }
                }
                else
                {
                    _consoleAllocated = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to allocate console: {ex.Message}");
            }
        }

        #region Level filtering

        public static AppLogLevel MinimumLogLevel { get; set; } = AppLogLevel.Info;
        public static bool EnableDebugLogging { get; set; } = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEnabled(AppLogLevel level) =>
            level == AppLogLevel.Debug ? EnableDebugLogging : level >= MinimumLogLevel;

        #endregion

        #region Core write

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLine(object data) =>
            Enqueue(data?.ToString() ?? string.Empty);

        public static void WriteBlock(List<string> lines)
        {
            // Held only while enqueueing, so the block stays contiguous in the output.
            lock (_writeLock)
                foreach (var line in lines)
                    Enqueue(line);
        }

        // Runs on the LogWriter thread only (so _currentColor needs no synchronization).
        // The timestamp is the caller's, captured at Enqueue time.
        private static void WriteCore(DateTime utc, string message)
        {
            var timestamp = utc.ToString("HH:mm:ss.fff");
            var formatted = $"[{timestamp}] {message}";

            Debug.WriteLine(formatted);

            if (_consoleAllocated)
            {
                var color = message.Contains("ERROR") || message.Contains("FAIL")
                    ? ConsoleColor.Red
                    : message.Contains("[IL2CPP]")
                    ? ConsoleColor.Green
                    : message.Contains("[GOM]") || message.Contains("[Signature]")
                    ? ConsoleColor.Yellow
                    : message.Contains("OK") || message.Contains("success")
                    ? ConsoleColor.Cyan
                    : ConsoleColor.Gray;

                if (color != _currentColor)
                {
                    Console.ForegroundColor = color;
                    _currentColor = color;
                }
                Console.WriteLine(formatted);
            }

            _fileWriter?.WriteLine(formatted);
        }

        #endregion

        #region Level-aware write

        public static void Write(AppLogLevel level, string message, string category = "")
        {
            if (!IsEnabled(level))
                return;

            var prefix = level switch
            {
                AppLogLevel.Error   => "ERROR ",
                AppLogLevel.Warning => "WARNING ",
                AppLogLevel.Debug   => "DEBUG ",
                _                   => ""
            };

            var line = (prefix.Length, string.IsNullOrEmpty(category)) switch
            {
                (0, true)  => message,
                (0, false) => $"[{category}] {message}",
                (_, true)  => $"{prefix}{message}",
                _          => $"{prefix}[{category}] {message}"
            };

            WriteLine(line);
        }

        #endregion

        #region Rate-limit helpers

        private static readonly ConcurrentDictionary<string, DateTime> _rateLimitCache = new();
        private static long _lastRateLimitCleanupTick;
        private const long RateLimitCleanupIntervalMs = 300_000; // 5 minutes
        private static readonly TimeSpan RateLimitStaleThreshold = TimeSpan.FromMinutes(5);

        public static void WriteRateLimited(AppLogLevel level, string key, TimeSpan interval, string message, string category = "")
        {
            if (!IsEnabled(level))
                return;
            var now = DateTime.UtcNow;
            if (_rateLimitCache.TryGetValue(key, out var last) && now - last < interval)
                return;
            _rateLimitCache[key] = now;
            Write(level, message, category);

            // Periodic eviction of stale entries to prevent unbounded growth.
            // (Mirrors the block below so the early-return path above still triggers cleanup.)
            MaybeCleanupRateLimitCache(now);
            return;
        }

        /// <summary>
        /// Cheap predicate variant of <see cref="WriteRateLimited"/> — returns <c>true</c>
        /// only when the message would actually be emitted. Allows hot-path callers to
        /// avoid building an interpolated message string on every tick when the rate-limit
        /// or level filter will discard it. Updates the rate-limit timestamp on success
        /// so the caller can follow up with a normal <see cref="Write"/> call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldEmitRateLimited(AppLogLevel level, string key, TimeSpan interval)
        {
            if (!IsEnabled(level))
                return false;
            var now = DateTime.UtcNow;
            if (_rateLimitCache.TryGetValue(key, out var last) && now - last < interval)
                return false;
            _rateLimitCache[key] = now;
            MaybeCleanupRateLimitCache(now);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaybeCleanupRateLimitCache(DateTime now)
        {

            // Periodic eviction of stale entries to prevent unbounded growth
            var nowTick = Environment.TickCount64;
            if (nowTick - Interlocked.Read(ref _lastRateLimitCleanupTick) > RateLimitCleanupIntervalMs)
            {
                Interlocked.Exchange(ref _lastRateLimitCleanupTick, nowTick);
                foreach (var kvp in _rateLimitCache)
                {
                    if (now - kvp.Value > RateLimitStaleThreshold)
                        _rateLimitCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        #endregion
    }
}
