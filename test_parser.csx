// Quick test: simulate what ZapretUpdater.ParseStrategies does for ALT3
using System.Text.RegularExpressions;

var zapretDir = @"C:\ProgramData\VPNRouter\zapret";
var binPath = ""; // relative to CWD (bin/)
var listsPath = @"..\lists\"; // relative to bin/

var batFile = Path.Combine(zapretDir, "general (ALT3).bat");
var lines = File.ReadAllLines(batFile);

var cmdBuilder = new System.Text.StringBuilder();
bool inCommand = false;

foreach (var rawLine in lines)
{
    var line = rawLine.TrimEnd();
    if (!inCommand)
    {
        if (line.Contains("winws.exe"))
        {
            inCommand = true;
            cmdBuilder.Append(line);
            if (!line.EndsWith("^")) break;
            cmdBuilder.Length -= 1;
        }
        continue;
    }
    cmdBuilder.Append(' ');
    if (line.EndsWith("^"))
        cmdBuilder.Append(line, 0, line.Length - 1);
    else { cmdBuilder.Append(line); break; }
}

var fullCmd = cmdBuilder.ToString();
var exeIdx = fullCmd.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
var afterExe = fullCmd.IndexOf('"', exeIdx);
var argsStart = afterExe >= 0 ? afterExe + 1 : exeIdx + "winws.exe".Length;
var args = fullCmd[argsStart..].Trim();

// Substitute
args = args.Replace("%BIN%", binPath, StringComparison.OrdinalIgnoreCase);
args = args.Replace("%LISTS%", listsPath, StringComparison.OrdinalIgnoreCase);
args = Regex.Replace(args, @",\s*%GameFilter\w+%", "", RegexOptions.IgnoreCase);
args = Regex.Replace(args, @"%GameFilter\w+%\s*,?", "", RegexOptions.IgnoreCase);

// Split by --new, filter empty filters
var segments = Regex.Split(args, @"\s+--new\s+");
var valid = new List<string>();
foreach (var seg in segments)
{
    var t = seg.Trim();
    if (string.IsNullOrWhiteSpace(t)) continue;
    if (Regex.IsMatch(t, @"--filter-(?:tcp|udp)=(?:\s|--)", RegexOptions.IgnoreCase)) continue;
    if (Regex.IsMatch(t, @"--filter-(?:tcp|udp)=$", RegexOptions.IgnoreCase)) continue;
    valid.Add(t);
}
args = string.Join(" --new ", valid);
args = Regex.Replace(args, @"\s+", " ").Trim();
args = args.Replace("\\\\", "\\");

Console.WriteLine("=== PARSED ARGS ===");
Console.WriteLine(args);
Console.WriteLine();

// Now write same bat as our app would
var batContent = $"@echo off\r\ncd /d \"{Path.Combine(zapretDir, "bin")}\"\r\nwinws.exe {args}\r\n";
var testBat = Path.Combine(zapretDir, "test_parsed_alt3.bat");
File.WriteAllText(testBat, batContent);
Console.WriteLine($"Wrote: {testBat}");
Console.WriteLine("Run it as admin to test!");
