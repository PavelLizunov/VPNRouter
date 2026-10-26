using System.Text.Json;

namespace VPNRouter.Headless.Protocol;

public static class ProtocolParser
{
    private static readonly JsonDocument s_emptyDoc = JsonDocument.Parse("{}");

    public static ProtocolRequest ParseRequest(ReadOnlyMemory<byte> utf8Memory, out string? extractedId)
    {
        extractedId = null;

        if (utf8Memory.Length > ProtocolConstants.MaxInputFrameBytes)
        {
            throw new RouterException("payload_too_large", "Input frame exceeds maximum allowed size.");
        }

        var readerOptions = new JsonReaderOptions
        {
            MaxDepth = ProtocolConstants.MaxJsonDepth,
            CommentHandling = JsonCommentHandling.Disallow
        };

        int? version = null;
        string? id = null;
        string? method = null;
        bool hasParams = false;

        var propertySets = new List<HashSet<string>>(ProtocolConstants.MaxJsonDepth);
        int objectDepth = 0;
        int rootObjectCount = 0;

        try
        {
            var reader = new Utf8JsonReader(utf8Memory.Span, readerOptions);

            if (!reader.Read())
            {
                throw new RouterException("invalid_request", "Empty request payload.");
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new RouterException("invalid_request", "Request must be a JSON object.");
            }

            // Root object started
            objectDepth = 1;
            rootObjectCount = 1;
            propertySets.Add(new HashSet<string>(StringComparer.Ordinal));

            while (reader.Read())
            {
                if (rootObjectCount >= 1 && objectDepth == 0)
                {
                    throw new RouterException("invalid_request", "Unexpected trailing data after JSON object.");
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectDepth++;
                        if (objectDepth > ProtocolConstants.MaxJsonDepth)
                        {
                            throw new RouterException("depth_limit_exceeded", "JSON nesting exceeds maximum allowed depth.");
                        }
                        if (propertySets.Count < objectDepth)
                        {
                            propertySets.Add(new HashSet<string>(StringComparer.Ordinal));
                        }
                        else
                        {
                            propertySets[objectDepth - 1].Clear();
                        }
                        break;

                    case JsonTokenType.EndObject:
                        if (objectDepth > 0 && objectDepth <= propertySets.Count)
                        {
                            propertySets[objectDepth - 1].Clear();
                        }
                        objectDepth--;
                        break;

                    case JsonTokenType.PropertyName:
                        string propName = reader.GetString()!;
                        if (objectDepth <= 0 || objectDepth > propertySets.Count)
                        {
                            throw new RouterException("invalid_request", "Malformed JSON structure.");
                        }

                        if (!propertySets[objectDepth - 1].Add(propName))
                        {
                            throw new RouterException("duplicate_key", "Duplicate JSON object key detected.");
                        }

                        if (objectDepth == 1)
                        {
                            // Root-level properties
                            switch (propName)
                            {
                                case "v":
                                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int vVal) || vVal != ProtocolConstants.ProtocolVersion)
                                    {
                                        throw new RouterException("invalid_version", "Unsupported protocol version.");
                                    }
                                    version = vVal;
                                    break;

                                case "id":
                                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                                    {
                                        throw new RouterException("invalid_id", "Request id must be 1-64 ASCII alphanumeric or hyphen characters.");
                                    }
                                    string idVal = reader.GetString()!;
                                    if (!ProtocolRequest.IsValidId(idVal))
                                    {
                                        throw new RouterException("invalid_id", "Request id must be 1-64 ASCII alphanumeric or hyphen characters.");
                                    }
                                    id = idVal;
                                    extractedId = idVal;
                                    break;

                                case "method":
                                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                                    {
                                        throw new RouterException("invalid_request", "Method must be a string.");
                                    }
                                    string methodVal = reader.GetString()!;
                                    if (string.IsNullOrWhiteSpace(methodVal))
                                    {
                                        throw new RouterException("invalid_request", "Method must not be empty.");
                                    }
                                    if (!ProtocolConstants.IsKnownMethod(methodVal))
                                    {
                                        throw new RouterException("unknown_method", "Unknown method requested.");
                                    }
                                    method = methodVal;
                                    break;

                                case "params":
                                    if (!reader.Read())
                                    {
                                        throw new RouterException("invalid_request", "Params property is incomplete.");
                                    }
                                    if (reader.TokenType == JsonTokenType.Null)
                                    {
                                        hasParams = false;
                                    }
                                    else if (reader.TokenType == JsonTokenType.StartObject)
                                    {
                                        hasParams = true;
                                        objectDepth++;
                                        if (objectDepth > ProtocolConstants.MaxJsonDepth)
                                        {
                                            throw new RouterException("depth_limit_exceeded", "JSON nesting exceeds maximum allowed depth.");
                                        }
                                        if (propertySets.Count < objectDepth)
                                        {
                                            propertySets.Add(new HashSet<string>(StringComparer.Ordinal));
                                        }
                                        else
                                        {
                                            propertySets[objectDepth - 1].Clear();
                                        }
                                    }
                                    else
                                    {
                                        throw new RouterException("invalid_request", "Params must be a JSON object.");
                                    }
                                    break;

                                default:
                                    throw new RouterException("invalid_request", "Unknown fields in request.");
                            }
                        }
                        break;
                }
            }
        }
        catch (JsonException jex)
        {
            if (jex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
            {
                throw new RouterException("depth_limit_exceeded", "JSON nesting exceeds maximum allowed depth.", jex);
            }
            throw new RouterException("invalid_request", "Malformed JSON payload.", jex);
        }

        if (objectDepth != 0)
        {
            throw new RouterException("invalid_request", "Incomplete JSON payload.");
        }

        if (version == null)
            throw new RouterException("invalid_version", "Protocol version 'v' is required.");
        if (id == null)
            throw new RouterException("invalid_id", "Request id is required.");
        if (method == null)
            throw new RouterException("invalid_request", "Method is required.");

        JsonElement paramsElement;
        if (hasParams)
        {
            using var doc = JsonDocument.Parse(utf8Memory, new JsonDocumentOptions { MaxDepth = ProtocolConstants.MaxJsonDepth });
            if (doc.RootElement.TryGetProperty("params", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
            {
                paramsElement = pElem.Clone();
            }
            else
            {
                paramsElement = s_emptyDoc.RootElement.Clone();
            }
        }
        else
        {
            paramsElement = s_emptyDoc.RootElement.Clone();
        }

        return new ProtocolRequest(version.Value, id, method, paramsElement);
    }

    public static ProtocolRequest ParseRequest(ReadOnlySpan<byte> utf8Bytes, out string? extractedId)
        => ParseRequest((ReadOnlyMemory<byte>)utf8Bytes.ToArray(), out extractedId);
}
