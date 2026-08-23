using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SonarrPatcher.Common
{
    internal enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    internal interface ILogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>
    /// Prefix-bound logger: <c>new Logger(prefix)</c> gives
    /// <see cref="Debug(string)"/>, <see cref="Info(string)"/>, <see cref="Warn(string)"/>
    /// and <see cref="Error(string)"/> methods, all funnelling into the single
    /// <see cref="LogSink.Instance"/> per process. The <c>SonarrPatcher.Common</c>
    /// source is linked into every hook assembly, so each DLL shares this exact type.
    /// </summary>
    internal sealed class Logger : ILogger
    {
        private readonly string _prefix;

        public Logger(string prefix)
        {
            _prefix = prefix;
        }

        public void Debug(string message) => LogSink.Instance.Log(LogLevel.Debug, _prefix, message);
        public void Info(string message) => LogSink.Instance.Log(LogLevel.Info, _prefix, message);
        public void Warn(string message) => LogSink.Instance.Log(LogLevel.Warn, _prefix, message);
        public void Error(string message) => LogSink.Instance.Log(LogLevel.Error, _prefix, message);
    }

    /// <summary>
    /// The single sink per process. Each hook DLL carries its own copy of this type
    /// (the Common source is linked into every assembly); the Loader marks its copy
    /// as the process-wide sink via <see cref="ClaimCanonical"/> and every other copy
    /// discovers it reflectively and delegates to it, so even with multiple hook DLLs
    /// there is one sink per process.
    /// <para>
    /// Until NLog is configured, messages are buffered in FIFO order and written to
    /// the console on <see cref="LogLevel.Error"/> (so startup failures show full
    /// context), via a one-shot <see cref="DumpDelay"/> timer, or at process exit.
    /// When NLog becomes available (detected reflectively once Sonarr's NzbDroneLogger
    /// has registered at least one target), the buffer is flushed through it and later
    /// messages are written directly, using the prefix as the NLog logger name,
    /// landing in Sonarr's console and log files.
    /// </para>
    /// </summary>
    internal sealed class LogSink
    {
        private const string LoaderAssemblyName = "SonarrPatcher.Loader";

        private static readonly bool IsLoaderCopy = string.Equals(
            typeof(LogSink).Assembly.GetName().Name, LoaderAssemblyName, StringComparison.OrdinalIgnoreCase);

        public static readonly LogSink Instance = new LogSink();

        /// <summary>
        /// Delay before the one-shot timer dumps buffered messages to the console
        /// (overridable for tests).
        /// </summary>
        internal static TimeSpan DumpDelay = TimeSpan.FromSeconds(5);

        private static Timer _timer;

        /// <summary>
        /// Set by the Loader assembly (<see cref="ClaimCanonical"/>) to mark its
        /// copy as the process-wide sink. Every other copy (e.g. a patch loaded
        /// dynamically by the Loader) delegates to it, so with the two DLLs there
        /// is still a single sink per process.
        /// </summary>
        public static bool IsCanonical;

        private static object _canonical;
        private static MethodInfo _canonicalLog;
        private static Type _canonicalLevelType;

        private readonly object _gate = new object();
        private readonly Queue<Entry> _buffer = new Queue<Entry>();
        private bool _nlogConfigured;
        private Writer _writer;

        static LogSink()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        /// <summary>
        /// Marks this copy as the canonical process-wide sink. Only meaningful in
        /// the Loader assembly; ignored everywhere else.
        /// </summary>
        public static void ClaimCanonical()
        {
            if (IsLoaderCopy)
            {
                IsCanonical = true;
            }
        }

        public void Log(LogLevel level, string prefix, string message)
        {
            var canonical = CanonicalOrNull();
            if (canonical != null && !ReferenceEquals(canonical, this))
            {
                try
                {
                    var levelArg = _canonicalLevelType != null
                        ? Enum.ToObject(_canonicalLevelType, (int)level)
                        : level;
                    _canonicalLog.Invoke(canonical, new object[] { levelArg, prefix, message });
                    return;
                }
                catch
                {
                    // Fall through to the local sink if delegation fails.
                }
            }

            lock (_gate)
            {
                if (!_nlogConfigured)
                {
                    if (TryCreateWriter(out var writer))
                    {
                        _nlogConfigured = true;
                        _writer = writer;
                        FlushLocked();
                    }
                    else
                    {
                        var wasEmpty = _buffer.Count == 0;
                        _buffer.Enqueue(new Entry(level, prefix, message));

                        if (level == LogLevel.Error)
                        {
                            DumpLocked();
                            CancelTimer();
                        }
                        else if (wasEmpty)
                        {
                            ArmTimer();
                        }

                        return;
                    }
                }

                _writer(level, prefix, message);
            }
        }

        /// <summary>
        /// Returns the single process-wide sink (the Loader assembly's copy once
        /// it has claimed the role), or null when this copy should act as the
        /// sink itself (the Loader copy, standalone mode, or before the Loader
        /// claims). Discovery is reflective so patches never reference the Loader.
        /// </summary>
        private object CanonicalOrNull()
        {
            if (_canonical != null)
            {
                return _canonical;
            }

            if (IsLoaderCopy)
            {
                return null;
            }

            var loader = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, LoaderAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (loader == null)
            {
                return null;
            }

            var type = loader.GetType("SonarrPatcher.Common.LogSink", false);
            if (type == null)
            {
                return null;
            }

            if (type.GetField("IsCanonical", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool? != true)
            {
                return null;
            }

            var instance = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var log = type.GetMethod("Log", BindingFlags.Public | BindingFlags.Instance);
            var levelType = loader.GetType("SonarrPatcher.Common.LogLevel", false);
            if (instance == null || log == null || levelType == null)
            {
                return null;
            }

            _canonical = instance;
            _canonicalLog = log;
            _canonicalLevelType = levelType;
            return instance;
        }

        internal static bool SimulateConfigured;
        internal static Action<LogLevel, string, string> TestSink;

        internal static void Reset()
        {
            lock (Instance._gate)
            {
                Instance._buffer.Clear();
                Instance._nlogConfigured = false;
                Instance._writer = null;
                SimulateConfigured = false;
                TestSink = null;
            }

            CancelTimer();

            _canonical = null;
            _canonicalLog = null;
            _canonicalLevelType = null;
        }

        internal static int BufferedCount
        {
            get
            {
                lock (Instance._gate)
                {
                    return Instance._buffer.Count;
                }
            }
        }

        internal static bool TimerArmed => _timer != null;

        internal static void DumpBufferedToConsole()
        {
            lock (Instance._gate)
            {
                Instance.DumpLocked();
            }
        }

        internal static void TriggerDumpTimerForTest()
        {
            OnDumpTimer(null);
        }

        private static void ArmTimer()
        {
            if (_timer == null)
            {
                _timer = new Timer(OnDumpTimer);
            }

            _timer.Change(DumpDelay, Timeout.InfiniteTimeSpan);
        }

        private static void CancelTimer()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        /// <summary>
        /// One-shot timer callback: guarantees buffered messages are delivered.
        /// No-op only when NLog is configured and nothing is buffered; otherwise
        /// flushes the buffer through NLog if it is configured, or dumps it to
        /// the console in order.
        /// </summary>
        private static void OnDumpTimer(object state)
        {
            lock (Instance._gate)
            {
                if (Instance._nlogConfigured && Instance._buffer.Count == 0)
                {
                    return;
                }

                if (Instance.TryCreateWriter(out var writer))
                {
                    Instance._nlogConfigured = true;
                    Instance._writer = writer;
                    Instance.FlushLocked();
                    return;
                }

                Instance.DumpLocked();
            }
        }

        private void DumpLocked()
        {
            while (_buffer.Count > 0)
            {
                var entry = _buffer.Dequeue();
                WriteConsole(entry.Level, entry.Prefix, entry.Message);
            }
        }

        private void FlushLocked()
        {
            while (_buffer.Count > 0)
            {
                var entry = _buffer.Dequeue();
                _writer(entry.Level, entry.Prefix, entry.Message);
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            OnDumpTimer(null);
        }

        private static void WriteConsole(LogLevel level, string prefix, string message)
        {
            Console.WriteLine($"[{level}] {prefix}: {message}");
        }

        private bool TryCreateWriter(out Writer writer)
        {
            if (SimulateConfigured && TestSink != null)
            {
                writer = (level, prefix, message) => TestSink(level, prefix, message);
                return true;
            }

            return TryCreateNLogWriter(out writer);
        }

        /// <summary>
        /// Reflectively probes whether NLog has been configured by Sonarr
        /// (NzbDroneLogger.Register adds at least one target) and, when it has,
        /// builds a writer that logs through NLog using the prefix as the logger
        /// name. Uses only reflection so this assembly keeps zero hard
        /// dependencies on NLog / Sonarr.Common.
        /// </summary>
        private static bool TryCreateNLogWriter(out Writer writer)
        {
            writer = null;

            var nlogAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "NLog", StringComparison.OrdinalIgnoreCase));
            if (nlogAssembly == null)
            {
                return false;
            }

            var logManagerType = nlogAssembly.GetType("NLog.LogManager", false);
            if (logManagerType == null)
            {
                return false;
            }

            var configuration = logManagerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (configuration == null)
            {
                return false;
            }

            var allTargets = configuration.GetType().GetProperty("AllTargets")?.GetValue(configuration) as ICollection;
            if (allTargets == null || allTargets.Count == 0)
            {
                return false;
            }

            var getLogger = logManagerType.GetMethod("GetLogger", new[] { typeof(string) });
            var loggerType = nlogAssembly.GetType("NLog.Logger", false);
            if (getLogger == null || loggerType == null)
            {
                return false;
            }

            var methods = new Dictionary<LogLevel, MethodInfo>
            {
                [LogLevel.Debug] = loggerType.GetMethod("Debug", new[] { typeof(string) }),
                [LogLevel.Info] = loggerType.GetMethod("Info", new[] { typeof(string) }),
                [LogLevel.Warn] = loggerType.GetMethod("Warn", new[] { typeof(string) }),
                [LogLevel.Error] = loggerType.GetMethod("Error", new[] { typeof(string) })
            };

            if (methods.Any(m => m.Value == null))
            {
                return false;
            }

            writer = (level, prefix, message) =>
            {
                var logger = getLogger.Invoke(null, new object[] { prefix });
                methods[level].Invoke(logger, new object[] { message });
            };

            return true;
        }

        private delegate void Writer(LogLevel level, string prefix, string message);

        private sealed class Entry
        {
            public Entry(LogLevel level, string prefix, string message)
            {
                Level = level;
                Prefix = prefix;
                Message = message;
            }

            public LogLevel Level;
            public string Prefix;
            public string Message;
        }
    }
}
