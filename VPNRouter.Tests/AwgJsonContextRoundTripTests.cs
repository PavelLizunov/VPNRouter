using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public class AwgJsonContextRoundTripTests
{
    [Fact]
    public void VlessServerEntryList_WithAwg_RoundTripsThroughAppJsonContext()
    {
        var original = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main-brat",
                Protocol = "amneziawg",
                Server = "104.194.156.93",
                Port = 51820,
                Awg = new AwgConfig
                {
                    PrivateKey = "XJRWW/WbfydGk7/7Kn3LLn+70XoT6se7SX9zUztOuKU=",
                    PeerPublicKey = "iLtvwNI8UxIFHB9wNjyMud7/nofHJ5IBZaMC/knnWT0=",
                    Address = new List<string> { "10.66.0.23/32" },
                    Keepalive = 25,
                    Jc = 7, Jmin = 52, Jmax = 166, S1 = 101, S2 = 115,
                    H1 = "1707807384", H4 = "1028816851",
                },
            },
        };

        var json = JsonSerializer.Serialize(original, AppJsonContext.Default.ListVlessServerEntry);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListVlessServerEntry);

        Assert.NotNull(back);
        var s = Assert.Single(back!);
        Assert.Equal("amneziawg", s.Protocol);
        Assert.Equal("104.194.156.93", s.Server);
        Assert.NotNull(s.Awg);
        Assert.Equal("XJRWW/WbfydGk7/7Kn3LLn+70XoT6se7SX9zUztOuKU=", s.Awg!.PrivateKey);
        Assert.Equal("iLtvwNI8UxIFHB9wNjyMud7/nofHJ5IBZaMC/knnWT0=", s.Awg.PeerPublicKey);
        Assert.Single(s.Awg.Address);
        Assert.Equal("10.66.0.23/32", s.Awg.Address[0]);
        Assert.Equal(25, s.Awg.Keepalive);
        Assert.Equal(7, s.Awg.Jc);
        Assert.Equal("1707807384", s.Awg.H1);
        Assert.Equal("1028816851", s.Awg.H4);
    }
}
