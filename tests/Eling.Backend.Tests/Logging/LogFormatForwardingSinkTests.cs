using System.Text;
using Eling.Backend.Logging;
using Eling.Core.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Eling.Backend.Tests.Logging;

public sealed class LogFormatForwardingSinkTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;

    public LogFormatForwardingSinkTests()
    {
        _dirA = Path.Combine(Path.GetTempPath(), "eling-fwd-a-" + Guid.NewGuid().ToString("N"));
        _dirB = Path.Combine(Path.GetTempPath(), "eling-fwd-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dirA)) Directory.Delete(_dirA, true); } catch { }
        try { if (Directory.Exists(_dirB)) Directory.Delete(_dirB, true); } catch { }
    }

    private static LogEvent Probe(string marker)
    {
        var parser = new MessageTemplateParser();
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            parser.Parse(marker),
            Enumerable.Empty<LogEventProperty>());
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Emit_WithNullInner_DoesNotThrow()
    {
        using var sink = new LogFormatForwardingSink();
        sink.Emit(Probe("hello")); // no inner — must be a silent no-op
        Assert.Null(sink.Inner);
    }

    [Fact]
    public void Emit_ForwardsToCurrentInner()
    {
        var inner = ElingLoggingConfig.CreateSink(_dirA, "fwd.log", "fwd", 7, LogFormat.Text);
        using var forwarder = new LogFormatForwardingSink(inner);
        Assert.Same(inner, forwarder.Inner);

        forwarder.Emit(Probe("via-forwarder"));

        Assert.Contains("via-forwarder", ReadShared(Path.Combine(_dirA, "fwd.log")));
    }

    [Fact]
    public void SetInner_SwapsTarget_NewWritesGoToNewInner()
    {
        var oldInner = ElingLoggingConfig.CreateSink(_dirA, "fwd.log", "fwd", 7, LogFormat.Text);
        var newInner = ElingLoggingConfig.CreateSink(_dirB, "fwd.log", "fwd", 7, LogFormat.Text);
        using var forwarder = new LogFormatForwardingSink(oldInner);

        try
        {
            forwarder.Emit(Probe("before"));
            forwarder.SetInner(newInner);
            Assert.Same(newInner, forwarder.Inner);
            forwarder.Emit(Probe("after"));

            Assert.Contains("before", ReadShared(Path.Combine(_dirA, "fwd.log")));
            Assert.DoesNotContain("after", ReadShared(Path.Combine(_dirA, "fwd.log")));
            Assert.Contains("after", ReadShared(Path.Combine(_dirB, "fwd.log")));
            Assert.DoesNotContain("before", ReadShared(Path.Combine(_dirB, "fwd.log")));
        }
        finally
        {
            // SetInner does not dispose the old inner; the caller owns it.
            oldInner.Dispose();
        }
    }

    [Fact]
    public void Dispose_DisposesInner()
    {
        var inner = ElingLoggingConfig.CreateSink(_dirA, "fwd.log", "fwd", 7, LogFormat.Text);
        var forwarder = new LogFormatForwardingSink(inner);

        forwarder.Dispose();

        Assert.Null(forwarder.Inner);

        // The inner really was disposed, not merely detached: a direct Emit on it
        // is now a no-op, so nothing reaches the file.
        inner.Emit(Probe("after-inner-dispose"));
        Assert.DoesNotContain("after-inner-dispose", ReadShared(Path.Combine(_dirA, "fwd.log")));

        // And the forwarder itself is a no-op afterwards.
        forwarder.Emit(Probe("after-forwarder-dispose"));
        Assert.DoesNotContain("after-forwarder-dispose", ReadShared(Path.Combine(_dirA, "fwd.log")));
    }

    [Fact]
    public async Task Concurrent_EmitAndSetInner_DoesNotThrow()
    {
        var innerA = ElingLoggingConfig.CreateSink(_dirA, "fwd.log", "fwd", 7, LogFormat.Text);
        var innerB = ElingLoggingConfig.CreateSink(_dirB, "fwd.log", "fwd", 7, LogFormat.Text);
        var forwarder = new LogFormatForwardingSink(innerA);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        const int iterations = 500;

        var emitters = new Task[2];
        for (var t = 0; t < emitters.Length; t++)
        {
            emitters[t] = Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    try { forwarder.Emit(Probe($"emitter-{t}-{i}")); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        var switcher = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                try { forwarder.SetInner(i % 2 == 0 ? innerA : innerB); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        // Fire-and-forget Dispose racing against the emitters/switcher.
        _ = Task.Run(() => forwarder.Dispose());

        try
        {
            await Task.WhenAll([emitters[0], emitters[1], switcher]);
        }
        finally
        {
            // Release both file handles so the fixture can delete the temp dirs.
            forwarder.Dispose();
            innerA.Dispose();
            innerB.Dispose();
        }

        Assert.Empty(exceptions);
    }
}
