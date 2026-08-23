using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SonarrPatcher.Common;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class LoggerTests : IDisposable
    {
        private readonly StringWriter _writer;
        private readonly TextWriter _original;

        public LoggerTests()
        {
            _original = Console.Out;
            _writer = new StringWriter();
            Console.SetOut(_writer);
            LogSink.Reset();
            LogSink.DumpDelay = TimeSpan.FromHours(1);
        }

        public void Dispose()
        {
            LogSink.Reset();
            Console.SetOut(_original);
            _writer.Dispose();
        }

        private string ConsoleOutput => _writer.ToString();

        [Fact]
        public void Info_WhenNLogNotConfigured_IsBufferedNotPrinted()
        {
            new Logger("SkyHookPatch").Info("patch applied");

            Assert.Equal("", ConsoleOutput);
            Assert.Equal(1, LogSink.BufferedCount);
        }

        [Fact]
        public void Warn_WhenNLogNotConfigured_IsBufferedNotPrinted()
        {
            new Logger("SkyHookPatch").Warn("warn");

            Assert.Equal("", ConsoleOutput);
            Assert.Equal(1, LogSink.BufferedCount);
        }

        [Fact]
        public void Error_WhenNLogNotConfigured_DumpsAllBufferedInOrderAndClears()
        {
            new Logger("a").Info("1");
            new Logger("b").Warn("2");
            new Logger("c").Error("3");

            Assert.Equal(
                "[Info] a: 1" + Environment.NewLine +
                "[Warn] b: 2" + Environment.NewLine +
                "[Error] c: 3" + Environment.NewLine,
                ConsoleOutput);
            Assert.Equal(0, LogSink.BufferedCount);
        }

        [Fact]
        public void Constructor_BindsPrefixOnEveryLevel()
        {
            var log = new Logger("SonarrPatcher.Loader");

            log.Error("loader fail");

            Assert.Equal("[Error] SonarrPatcher.Loader: loader fail" + Environment.NewLine, ConsoleOutput);
        }

        [Fact]
        public void WhenConfigured_FlushesBufferedInOrderThenWritesDirectly()
        {
            var received = new List<(LogLevel Level, string Prefix, string Message)>();
            LogSink.TestSink = (level, prefix, message) => received.Add((level, prefix, message));

            new Logger("a").Info("1");
            new Logger("b").Warn("2");

            Assert.Equal(2, LogSink.BufferedCount);
            Assert.Equal("", ConsoleOutput);

            LogSink.SimulateConfigured = true;

            new Logger("c").Info("3");

            Assert.Equal(0, LogSink.BufferedCount);
            Assert.Equal(3, received.Count);
            Assert.Equal((LogLevel.Info, "a", "1"), received[0]);
            Assert.Equal((LogLevel.Warn, "b", "2"), received[1]);
            Assert.Equal((LogLevel.Info, "c", "3"), received[2]);

            new Logger("d").Debug("4");

            Assert.Equal(4, received.Count);
            Assert.Equal((LogLevel.Debug, "d", "4"), received[3]);
            Assert.Equal(0, LogSink.BufferedCount);
        }

        [Fact]
        public void Info_BuffersAndArmsOneShotTimer()
        {
            new Logger("x").Info("pending");

            Assert.True(LogSink.TimerArmed);
            Assert.Equal(1, LogSink.BufferedCount);

            LogSink.Reset();

            Assert.False(LogSink.TimerArmed);
        }

        [Fact]
        public void TimerCallback_WhenNotConfigured_DumpsBufferedInOrderAndClears()
        {
            new Logger("a").Info("1");
            new Logger("b").Warn("2");

            LogSink.TriggerDumpTimerForTest();

            Assert.Equal(
                "[Info] a: 1" + Environment.NewLine +
                "[Warn] b: 2" + Environment.NewLine,
                ConsoleOutput);
            Assert.Equal(0, LogSink.BufferedCount);
        }

        [Fact]
        public void TimerCallback_WhenConfiguredWithCache_FlushesToNLog()
        {
            var received = new List<(LogLevel Level, string Prefix, string Message)>();
            LogSink.TestSink = (level, prefix, message) => received.Add((level, prefix, message));

            new Logger("x").Info("pending");
            Assert.Equal(1, LogSink.BufferedCount);

            LogSink.SimulateConfigured = true;
            LogSink.TriggerDumpTimerForTest();

            Assert.Equal(0, LogSink.BufferedCount);
            Assert.Single(received);
            Assert.Equal((LogLevel.Info, "x", "pending"), received[0]);
        }

        [Fact]
        public void TimerCallback_WhenConfiguredAndEmpty_NoOp()
        {
            var received = new List<(LogLevel Level, string Prefix, string Message)>();
            LogSink.TestSink = (level, prefix, message) => received.Add((level, prefix, message));
            LogSink.SimulateConfigured = true;

            LogSink.TriggerDumpTimerForTest();

            Assert.Empty(received);
            Assert.Equal(0, LogSink.BufferedCount);
        }

        [Fact]
        public void DumpBufferedToConsole_EmitsBufferedEntries()
        {
            new Logger("x").Info("pending");

            LogSink.DumpBufferedToConsole();

            Assert.Equal("[Info] x: pending" + Environment.NewLine, ConsoleOutput);
            Assert.Equal(0, LogSink.BufferedCount);
        }

        [Fact]
        public void WhenLoaderClaimsCanonical_LogsDelegateToLoaderSink()
        {
            LogSink.Reset();

            var loaderAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "SonarrPatcher.Loader");
            var loaderSinkType = loaderAssembly.GetType("SonarrPatcher.Common.LogSink");
            Assert.NotNull(loaderSinkType);

            try
            {
                loaderSinkType.GetMethod("ClaimCanonical", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

                var before = (int)loaderSinkType
                    .GetProperty("BufferedCount", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);

                new Logger("SkyHookPatch").Info("delegated");

                var after = (int)loaderSinkType
                    .GetProperty("BufferedCount", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);
                Assert.True(after > before, "Loader sink should have received the delegated message");
                Assert.Equal(0, LogSink.BufferedCount);
            }
            finally
            {
                loaderSinkType.GetField("IsCanonical", BindingFlags.Public | BindingFlags.Static).SetValue(null, false);
                LogSink.Reset();
            }
        }
    }
}
