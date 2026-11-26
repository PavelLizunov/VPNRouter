#nullable enable
using System;
using System.Text;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ClashLogMessageBufferTests
{
    private const string EofJson =
        "{\"type\":\"error\",\"payload\":\"+0300 2026-06-19 15:50:54 ERROR [810041638 108ms] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF\"}";

    [Fact]
    public void Message_AcrossFragments_IsDecoded()
    {
        var bytes = Encoding.UTF8.GetBytes(EofJson);
        var buffer = new ClashLogMessageBuffer(1024);

        var first = bytes.Length / 3;
        var second = bytes.Length / 2;
        buffer.Append(bytes.AsSpan(0, first));
        buffer.Append(bytes.AsSpan(first, second - first));
        buffer.Append(bytes.AsSpan(second));

        Assert.Equal(EofJson, buffer.Complete());
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.False(buffer.IsOversized);
    }

    [Fact]
    public void OversizedMessage_IsDropped_AndNextMessageSurvives()
    {
        var buffer = new ClashLogMessageBuffer(32);

        buffer.Append(new byte[33]);
        Assert.True(buffer.IsOversized);
        Assert.Equal(0, buffer.BufferedBytes);
        Assert.Null(buffer.Complete());

        buffer.Append(Encoding.UTF8.GetBytes("{\"payload\":\"next\"}"));
        Assert.Equal("{\"payload\":\"next\"}", buffer.Complete());
    }

    [Fact]
    public void OversizedAcrossFragments_DoesNotAccumulate()
    {
        var buffer = new ClashLogMessageBuffer(16);

        buffer.Append(new byte[8]);
        buffer.Append(new byte[9]);

        Assert.True(buffer.IsOversized);
        Assert.Equal(0, buffer.BufferedBytes);

        for (var i = 0; i < 1000; i++)
            buffer.Append(new byte[1024]);

        Assert.Equal(0, buffer.BufferedBytes);
        Assert.Null(buffer.Complete());
    }

    [Fact]
    public void ExactCeiling_IsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes("{\"payload\":\"ok\"}");
        var buffer = new ClashLogMessageBuffer(payload.Length);

        buffer.Append(payload);

        Assert.Equal("{\"payload\":\"ok\"}", buffer.Complete());
    }

    [Fact]
    public void OneByteOverCeiling_IsDropped()
    {
        var buffer = new ClashLogMessageBuffer(4);

        buffer.Append(new byte[] { 1, 2, 3, 4 });
        buffer.Append(new byte[] { 5 });

        Assert.True(buffer.IsOversized);
        Assert.Null(buffer.Complete());
    }

    [Fact]
    public void MultibyteCharacterSplitAcrossFragments_DecodesOnce()
    {
        const string text = "{\"payload\":\"привет\"}";
        var bytes = Encoding.UTF8.GetBytes(text);
        var split = Array.IndexOf(bytes, (byte)0xD0) + 1;
        Assert.InRange(split, 1, bytes.Length - 1);

        var buffer = new ClashLogMessageBuffer(1024);
        buffer.Append(bytes.AsSpan(0, split));
        buffer.Append(bytes.AsSpan(split));

        Assert.Equal(text, buffer.Complete());
    }

    [Fact]
    public void Reset_DiscardsPartialMessage()
    {
        var buffer = new ClashLogMessageBuffer(64);
        buffer.Append(Encoding.UTF8.GetBytes("{\"payload\":\"stale\""));

        buffer.Reset();
        buffer.Append(Encoding.UTF8.GetBytes("{\"payload\":\"next\"}"));

        Assert.Equal("{\"payload\":\"next\"}", buffer.Complete());
    }

    [Fact]
    public void OversizedMessage_NeverReachesClassifier_ButNextMessageDoes()
    {
        var state = new ConnectionHealthState();
        var stream = new ClashLogStream("http://127.0.0.1:9090", state);
        var bytes = Encoding.UTF8.GetBytes(EofJson);

        var oversized = new ClashLogMessageBuffer(bytes.Length - 1);
        oversized.Append(bytes);
        Assert.Null(oversized.Complete());

        var next = new ClashLogMessageBuffer(bytes.Length);
        next.Append(bytes);
        var text = next.Complete();
        Assert.NotNull(text);
        stream.HandleMessage(text!);

        Assert.Equal(1, state.Snapshot().RelayOpenFails);
    }
}
