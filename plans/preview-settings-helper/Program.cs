using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

internal static class Program
{
    // AppConfig verifies these three defaults are false. start_minimized is NOT
    // an AppConfig member; preserve it and every other unknown field unchanged.
    // The remote caller, not this platform-independent helper, guards WINBRAT.
    private static readonly string[] Keys = ["autostart_vpn", "autostart_zapret", "autostart_tgproxy"];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private enum Stage { Arguments, ReadInput, DecodeUtf8, ParseEvents, LoadDocument, ValidateMapping, ValidateApp, ValidateBoolean, SourceSpans, Reparse, CreateOutput, WriteOutput }
    private enum Reason { Rejected, InvalidUtf8, AccessDenied, IoFailure, YamlSyntaxOrDuplicate, Alias, Anchor, Tag, Depth, RootMapping, AppMapping, BooleanScalar, MappingKeyOrDuplicate, SourceSpan, ReparseMismatch }
    private static Stage stage;
    private sealed class Rejected(Reason reason) : Exception
    {
        public Reason Reason { get; } = reason;
    }
    private static string Category(Exception error) => error switch
    {
        Rejected rejected => rejected.Reason.ToString(),
        DecoderFallbackException => Reason.InvalidUtf8.ToString(),
        UnauthorizedAccessException => Reason.AccessDenied.ToString(),
        YamlException => Reason.YamlSyntaxOrDuplicate.ToString(),
        IOException => Reason.IoFailure.ToString(),
        _ => Reason.Rejected.ToString()
    };

    private static int Main(string[] args)
    {
        bool inspect = args.Length > 0 && args[0] == "--inspect";
        try
        {
            if (args is ["--self-test"])
            {
                SelfTest();
                Console.WriteLine("self_test=true");
                return 0;
            }
            if (inspect)
            {
                // Read-only: exercise the exact transform in memory, never open output.
                if (args.Length != 2) throw new InvalidDataException();
                stage = Stage.ReadInput;
                Transform(File.ReadAllBytes(args[1]));
                Console.WriteLine("inspect=true validation=true");
                return 0;
            }
            if (args.Length != 2) throw new InvalidDataException();
            var input = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[1]);
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            stage = Stage.ReadInput;
            var result = Transform(File.ReadAllBytes(input));
            stage = Stage.CreateOutput;
            // Never truncate or overwrite a destination (including existing links).
            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(result);
                file.Flush(true);
            }
            Console.WriteLine("success=true autostart_vpn=false autostart_zapret=false autostart_tgproxy=false");
            return 0;
        }
        catch (Exception error)
        {
            // Emit only closed enums, never exception text or YAML content.
            Console.Error.WriteLine(inspect ? $"inspect=false stage={stage} reason={Category(error)}" : "success=false");
            return 1;
        }
    }

    private static byte[] Transform(byte[] original)
    {
        int bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var text = Utf8.GetString(original, bom, original.Length - bom);
        stage = Stage.ParseEvents;
        var before = Inspect(text);
        stage = Stage.SourceSpans;
        using var output = new MemoryStream();
        int cursor = 0;
        foreach (var scalar in before.Values.OrderBy(s => s.Start.Index))
        {
            int start = checked((int)scalar.Start.Index);
            int end = checked((int)scalar.End.Index);
            if (start < 0 || end < start || end > text.Length) throw new InvalidDataException();
            var source = text[start..end];
            if (source != scalar.Value || (source != "true" && source != "false"))
                throw new InvalidDataException();
            int byteStart = bom + Utf8.GetByteCount(text.AsSpan(0, start));
            int byteEnd = bom + Utf8.GetByteCount(text.AsSpan(0, end));
            if (byteStart < cursor) throw new InvalidDataException();
            output.Write(original.AsSpan(cursor, byteStart - cursor));
            output.Write("false"u8);
            cursor = byteEnd;
        }
        output.Write(original.AsSpan(cursor));
        var result = output.ToArray();
        var after = Inspect(Utf8.GetString(result, bom, result.Length - bom));
        if (!before.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(after.Keys) ||
            after.Values.Any(s => s.Value != "false")) throw new InvalidDataException();
        return result;
    }

    private static Dictionary<string, YamlScalarNode> Inspect(string text)
    {
        // References outside the edited mapping are legal YAML. Record their
        // targets before resolution so edits cannot change aliased values elsewhere.
        var parser = new Parser(new StringReader(text));
        int depth = 0;
        var references = new HashSet<AnchorName>();
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias alias) references.Add(alias.Value);
            if (parser.Current is NodeEvent node && !node.Tag.IsEmpty)
                throw new Rejected(Reason.Tag);
            if (parser.Current is MappingStart or SequenceStart && ++depth > 64)
                throw new InvalidDataException();
            if (parser.Current is MappingEnd or SequenceEnd) --depth;
        }
        stage = Stage.LoadDocument;
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        stage = Stage.ValidateMapping;
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException();
        Validate(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance),
            new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        stage = Stage.ValidateApp;
        var app = root.Children.SingleOrDefault(p => ((YamlScalarNode)p.Key).Value == "app").Value;
        if (app is not YamlMappingNode mapping) throw new InvalidDataException();
        if (references.Contains(mapping.Anchor)) throw new Rejected(Reason.Alias);
        var found = new Dictionary<string, YamlScalarNode>(StringComparer.Ordinal);
        stage = Stage.ValidateBoolean;
        foreach (var pair in mapping.Children)
        {
            var key = ((YamlScalarNode)pair.Key).Value!;
            if (!Keys.Contains(key, StringComparer.Ordinal)) continue;
            if (pair.Value is not YamlScalarNode scalar || scalar.Style != ScalarStyle.Plain ||
                (scalar.Value != "true" && scalar.Value != "false")) throw new InvalidDataException();
            if (references.Contains(scalar.Anchor)) throw new Rejected(Reason.Alias);
            found.Add(key, scalar);
        }
        return found;
    }

    private static void Validate(YamlNode node, HashSet<YamlNode> active, HashSet<YamlNode> visited)
    {
        if (active.Contains(node)) throw new Rejected(Reason.Alias);
        if (!visited.Add(node)) return;
        if (active.Count >= 64) throw new Rejected(Reason.Depth);
        active.Add(node);
        if (node is YamlMappingNode mapping)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null ||
                    key.Value == "<<" || !names.Add(key.Value)) throw new InvalidDataException();
                Validate(pair.Value, active, visited);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children) Validate(child, active, visited);
        }
        else if (node is not YamlScalarNode) throw new InvalidDataException();
        active.Remove(node);
    }

    private static void SelfTest()
    {
        void Equal(string source, string expected)
        {
            if (!Transform(Utf8.GetBytes(source)).AsSpan().SequenceEqual(Utf8.GetBytes(expected)))
                throw new InvalidDataException();
        }
        void Reject(string source)
        {
            try { Transform(Utf8.GetBytes(source)); }
            catch (Exception) { return; }
            throw new InvalidDataException();
        }
        Equal("app: {autostart_vpn: true, autostart_zapret: false, autostart_tgproxy: true}\n",
              "app: {autostart_vpn: false, autostart_zapret: false, autostart_tgproxy: false}\n");
        Equal("\ufeff# unicode é \U0001F680\r\napp:\r\n  autostart_vpn: true # keep\r\n  nested: {autostart_vpn: true}\r\n  text: 'autostart_vpn: true'\r\n  start_minimized: true\r\n",
              "\ufeff# unicode é \U0001F680\r\napp:\r\n  autostart_vpn: false # keep\r\n  nested: {autostart_vpn: true}\r\n  text: 'autostart_vpn: true'\r\n  start_minimized: true\r\n");
        Equal("app: {}\nother: true\n", "app: {}\nother: true\n");
        Equal("app:\n  autostart_vpn: false\n", "app:\n  autostart_vpn: false\n");
        Equal("shared: &a {nested: [one, two]}\ncopy: *a\napp: {autostart_vpn: true, autostart_zapret: true, autostart_tgproxy: true}\n",
              "shared: &a {nested: [one, two]}\ncopy: *a\napp: {autostart_vpn: false, autostart_zapret: false, autostart_tgproxy: false}\n");
        Equal("shared: &a true\napp: {other: *a, autostart_vpn: true}\n",
              "shared: &a true\napp: {other: *a, autostart_vpn: false}\n");
        Reject("app: &a {autostart_vpn: true}\ncopy: *a");
        Reject("shared: &a {autostart_vpn: true}\napp: *a");
        Reject("app: {autostart_vpn: &a true}\ncopy: *a");
        Reject("app: {autostart_vpn: &a true, autostart_zapret: *a}");
        Reject("app: {}\ncycle: &a [*a]");
        Reject("app: {}\nshared: &a {same: 1, same: 2}\ncopy: *a");
        Reject("app: {}\nx: *missing");
        Reject("app: {}\nx: !!str text");
        Reject("app: {autostart_vpn: true, autostart_vpn: false}");
        Reject("app: {}\napp: {}");
        Reject("app: {}\nx: {same: 1, same: 2}");
        Reject("app: {autostart_vpn: 'true'}");
        Reject("app: {autostart_vpn: TRUE}");
        Reject("app: {autostart_vpn: 1}");
        Reject("app: {autostart_vpn: null}");
        Reject("app: {autostart_vpn: !!bool true}");
        Reject("value: &a true\napp: {autostart_vpn: *a}");
        Reject("app: {<<: {autostart_vpn: true}}");
        Reject("other: true");
        Reject("app: []");
        Reject("app: {}\n---\napp: {}");
        try { Transform([0xff, 0xfe, 0x61, 0]); }
        catch (DecoderFallbackException) { return; }
        throw new InvalidDataException();
    }
}
