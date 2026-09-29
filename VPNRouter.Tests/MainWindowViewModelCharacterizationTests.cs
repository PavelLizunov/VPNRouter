#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VPNRouter.Tests;

public class MainWindowViewModelCharacterizationTests
{
    private const string PinnedHashWindows =
        "dd8d24e1576f9427ef96d6981e7d5d3a2c4611937574884d3720ed96309945c6";

    private const string PinnedHashLinux =
        "0957f87818ec399f865fd78e1a3d1d409b867e89568659c8f11ad81ccdb1b706";

    private static string ComputeHash(string[] descriptions)
    {
        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }
}
