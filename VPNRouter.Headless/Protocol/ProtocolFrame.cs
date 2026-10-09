using System.Buffers;
using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

public record ProtocolError(string Code, string Message);

public abstract class ProtocolFrame
{
    public abstract byte[] ToUtf8Bytes();

    protected static byte[] SerializeWithLimit(Action<Utf8JsonWriter> writeAction, string? fallbackId)
    {
        try
        {
            var bufferWriter = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(bufferWriter))
            {
                writeAction(writer);
            }

            var outputBytes = bufferWriter.WrittenSpan.ToArray();
            if (outputBytes.Length <= ProtocolConstants.MaxOutputFrameBytes)
            {
                return outputBytes;
            }

            // Exceeded 256 KiB limit: replace with safe bounded internal_error frame
            return SerializeError(fallbackId, "internal_error", "Response size exceeds limit.");
        }
        catch
        {
            return SerializeError(fallbackId, "internal_error", RouterException.GetSafeMessage("internal_error"));
        }
    }

    public static byte[] SerializeError(string? id, string code, string message)
    {
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolConstants.ProtocolVersion);
            if (id != null)
                writer.WriteString("id", id);
            else
                writer.WriteNull("id");

            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return bufferWriter.WrittenSpan.ToArray();
    }
}

public sealed class ProtocolResponseFrame : ProtocolFrame
{
    public string? Id { get; }
    public object? Result { get; }
    public ProtocolError? Error { get; }

    private ProtocolResponseFrame(string? id, object? result, ProtocolError? error)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    public static ProtocolResponseFrame CreateSuccess(string id, object? result)
        => new(id, result, null);

    public static ProtocolResponseFrame CreateError(string? id, string code, string message)
        => new(id, null, new ProtocolError(code, message));

    public override byte[] ToUtf8Bytes()
    {
        if (Error != null)
        {
            return SerializeError(Id, Error.Code, Error.Message);
        }

        return SerializeWithLimit(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolConstants.ProtocolVersion);
            writer.WriteString("id", Id);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, Result, Result?.GetType() ?? typeof(object));
            writer.WriteEndObject();
        }, Id);
    }
}

public sealed class ProtocolStateEventFrame : ProtocolFrame
{
    public object StateData { get; set; }

    public ProtocolStateEventFrame(object stateData)
    {
        StateData = stateData ?? throw new ArgumentNullException(nameof(stateData));
    }

    public override byte[] ToUtf8Bytes()
    {
        return SerializeWithLimit(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolConstants.ProtocolVersion);
            writer.WriteString("event", "state");
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, StateData, StateData.GetType());
            writer.WriteEndObject();
        }, null);
    }
}

public sealed class ProtocolProgressEventFrame : ProtocolFrame
{
    public object ProgressData { get; set; }

    public ProtocolProgressEventFrame(object progressData)
    {
        ProgressData = progressData ?? throw new ArgumentNullException(nameof(progressData));
    }

    public override byte[] ToUtf8Bytes()
    {
        return SerializeWithLimit(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolConstants.ProtocolVersion);
            writer.WriteString("event", "progress");
            writer.WritePropertyName("data");
            JsonSerializer.Serialize(writer, ProgressData, ProgressData.GetType());
            writer.WriteEndObject();
        }, null);
    }
}
