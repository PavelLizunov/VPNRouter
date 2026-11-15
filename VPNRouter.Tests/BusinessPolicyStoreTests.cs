using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyStoreTests
{
    [Fact]
    public void Accept_ThenRestart_RevalidatesExactSignedPolicy()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        Assert.Null(store.Load());
        var accepted = store.Accept(fixture.Envelope(8, applyNow: true));
        Assert.Equal(8, accepted.Revision);
        Assert.True(accepted.ApplyNow);
        var reloaded = fixture.Store().Load();
        Assert.NotNull(reloaded);
        Assert.Equal(accepted.PayloadDigest, reloaded.PayloadDigest);
        Assert.Equal("proxy", Assert.Single(reloaded.Rules).Action);
        Assert.Equal(8, reloaded.Revision);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.JournalPath)!, "*.tmp"));
    }

    [Fact]
    public void Accept_RejectsReplayAndEqualRevisionEquivocation_WithoutChangingJournal()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var accepted = store.Accept(fixture.Envelope(8));
        var persisted = File.ReadAllBytes(store.JournalPath);
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(7)));
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(8, action: "direct")));
        Assert.Equal(persisted, File.ReadAllBytes(store.JournalPath));
        Assert.Same(accepted, store.Current);
    }

    [Fact]
    public void Accept_EqualPayloadWithAnotherSignature_IsIdempotent()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var first = store.Accept(fixture.Envelope(8));
        var journal = File.ReadAllBytes(store.JournalPath);
        var second = store.Accept(fixture.Envelope(8));
        Assert.Equal(first.PayloadDigest, second.PayloadDigest);
        Assert.Equal(journal, File.ReadAllBytes(store.JournalPath));
    }

    [Fact]
    public void Accept_InvalidSignatureOrIdentity_CannotReplaceValidRules()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        store.Accept(fixture.Envelope(8));
        var persisted = File.ReadAllBytes(store.JournalPath);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(9, signingKey: other)));
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(9, policyId: "other-policy")));
        Assert.Equal(persisted, File.ReadAllBytes(store.JournalPath));
        Assert.Equal(8, store.Current!.Revision);
    }

    [Fact]
    public void Accept_AtomicReplacementFailure_DoesNotAdvanceAcceptance()
    {
        using var fixture = new Fixture();
        var failed = false;
        var store = fixture.Store((source, target) =>
        {
            if (failed) throw new IOException("synthetic-private-write-failure");
            File.Move(source, target, overwrite: true);
        });
        var old = store.Accept(fixture.Envelope(8));
        var before = File.ReadAllBytes(store.JournalPath);
        failed = true;
        var error = Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(9)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-private", error.Message);
        Assert.Same(old, store.Current);
        Assert.Equal(before, File.ReadAllBytes(store.JournalPath));
        Assert.Equal(8, fixture.Store().Load()!.Revision);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(store.JournalPath)!, "*.tmp"));
    }

    [Fact]
    public void Accept_DirectorySyncFailureAfterRename_DoesNotAdvertiseUncertainCommit()
    {
        using var fixture = new Fixture();
        var failSync = false;
        var store = fixture.Store(flushDirectory: directory =>
        {
            if (failSync && Path.GetFileName(directory) == "policy")
                throw new IOException("synthetic-directory-sync-failure");
            BusinessPolicyDurability.FlushDirectory(directory);
        });
        var first = store.Accept(fixture.Envelope(8));
        failSync = true;
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(9)));
        Assert.Same(first, store.Current);
        // A failed flush can leave the newer complete file. Re-loading it must
        // also establish durability before moving the in-memory acceptance floor.
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(9)));
        Assert.Same(first, store.Current);
        failSync = false;
        Assert.Equal(9, store.Load()!.Revision);
        Assert.Throws<InvalidDataException>(() => fixture.Store().Accept(fixture.Envelope(8)));
        Assert.Equal(9, fixture.Store().Load()!.Revision);
    }

    [Fact]
    public void Accept_ParentDirectorySyncFailure_DoesNotCreateACommittedJournal()
    {
        using var fixture = new Fixture();
        var store = fixture.Store(flushDirectory: _ => throw new IOException("synthetic-sync-failure"));
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(8)));
        Assert.Null(store.Current);
        Assert.False(File.Exists(store.JournalPath));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("header")]
    [InlineData("digest")]
    public void Load_DamagedExistingJournal_BlocksReplacementInsteadOfResettingFloor(string damage)
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var accepted = store.Accept(fixture.Envelope(8));
        var bytes = File.ReadAllBytes(store.JournalPath);
        switch (damage)
        {
            case "truncated": bytes = bytes[..^1]; break;
            case "oversized": bytes = new byte[BusinessPolicy.MaximumResponseBytes * 2]; break;
            case "header": bytes[0] ^= 1; break;
            case "digest": bytes[48] ^= 1; break;
        }
        File.WriteAllBytes(store.JournalPath, bytes);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Same(accepted, store.Current);
        var fresh = fixture.Store();
        Assert.Throws<InvalidDataException>(() => fresh.Load());
        Assert.Throws<InvalidDataException>(() => fresh.Accept(fixture.Envelope(9)));
        Assert.Equal(bytes, File.ReadAllBytes(store.JournalPath));
    }

    [Fact]
    public void Load_DeletedOrRegressedJournal_CannotLowerLiveAcceptance()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        store.Accept(fixture.Envelope(8));
        var old = File.ReadAllBytes(store.JournalPath);
        store.Accept(fixture.Envelope(9));
        File.WriteAllBytes(store.JournalPath, old);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(10)));
        File.Delete(store.JournalPath);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Throws<InvalidDataException>(() => store.Accept(fixture.Envelope(10)));
        Assert.Equal(9, store.Current!.Revision);
    }

    [Fact]
    public void Load_RemovedSigningKey_DoesNotCreateANewRevisionNamespace()
    {
        using var fixture = new Fixture();
        var original = fixture.Store();
        original.Accept(fixture.Envelope(8));
        var before = File.ReadAllBytes(original.JournalPath);
        using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rotated = new BusinessPolicyStore(BusinessPolicyConfigurationTests.Configuration(replacement), fixture.Directory);
        Assert.Equal(original.JournalPath, rotated.JournalPath);
        Assert.Throws<InvalidDataException>(() => rotated.Load());
        Assert.Throws<InvalidDataException>(() => rotated.Accept(fixture.Envelope(9, signingKey: replacement)));
        Assert.Equal(before, File.ReadAllBytes(original.JournalPath));
    }

    [Fact]
    public void Accept_OverlappingKeyRotation_PreservesFloorAndAllowsNewSigner()
    {
        using var fixture = new Fixture();
        fixture.Store().Accept(fixture.Envelope(8));
        using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var secondKey = JsonSerializer.Serialize(new { id = "next-key", spki = Convert.ToBase64String(replacement.ExportSubjectPublicKeyInfo()) });
        var policyJson = BusinessPolicyConfigurationTests.PolicyJson(fixture.SigningKey)
            .Replace("\"keys\":[", "\"keys\":[" + secondKey + ",");
        var configuration = BusinessClientConfiguration.Parse("{\"business\":{\"policy\":" + policyJson + "}}").Policy!;
        var overlap = new BusinessPolicyStore(configuration, fixture.Directory);
        Assert.Equal(8, overlap.Load()!.Revision);
        Assert.Throws<InvalidDataException>(() => overlap.Accept(fixture.Envelope(7, signingKey: replacement, keyId: "next-key")));
        Assert.Equal(9, overlap.Accept(fixture.Envelope(9, signingKey: replacement, keyId: "next-key")).Revision);
        var rotatedJson = BusinessPolicyConfigurationTests.PolicyJson(replacement).Replace("fixture-key", "next-key");
        var rotated = BusinessClientConfiguration.Parse("{\"business\":{\"policy\":" + rotatedJson + "}}").Policy!;
        var afterRotation = new BusinessPolicyStore(rotated, fixture.Directory);
        Assert.Equal(overlap.JournalPath, afterRotation.JournalPath);
        Assert.Equal(9, afterRotation.Load()!.Revision);
    }

    [Fact]
    public void Accept_IndependentInstancesRecheckTheOnDiskFloor()
    {
        using var fixture = new Fixture();
        var first = fixture.Store();
        var second = fixture.Store();
        first.Accept(fixture.Envelope(8));
        second.Load();
        first.Accept(fixture.Envelope(10));
        Assert.Throws<InvalidDataException>(() => second.Accept(fixture.Envelope(9)));
        Assert.Equal(10, second.Load()!.Revision);
    }

    [Fact]
    public async Task Accept_ConcurrentPublisherCannotPassAWriterHoldingTheLease()
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = fixture.Store((source, destination) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Fixture release timed out.");
            File.Move(source, destination, overwrite: true);
        });
        var candidate = fixture.Envelope(8);
        var next = fixture.Envelope(9);
        var publication = Task.Run(() => first.Accept(candidate));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidDataException>(() => fixture.Store().Accept(next));
        }
        finally { release.Set(); }
        await publication;
        Assert.Equal(9, fixture.Store().Accept(next).Revision);
        Assert.Equal(9, fixture.Store().Load()!.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadAndAccept_RefuseLinkedJournalIncludingDanglingLinks(bool dangling)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links require Unix or a Windows privilege grant.");
        using var fixture = new Fixture();
        var store = fixture.Store();
        store.Accept(fixture.Envelope(8));
        var original = File.ReadAllBytes(store.JournalPath);
        var target = Path.Combine(fixture.Directory, "outside.bin");
        if (!dangling) File.WriteAllBytes(target, original);
        File.Delete(store.JournalPath);
        File.CreateSymbolicLink(store.JournalPath, target);
        Assert.Throws<InvalidDataException>(() => fixture.Store().Load());
        Assert.Throws<InvalidDataException>(() => fixture.Store().Accept(fixture.Envelope(9)));
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void Accept_JournalAndDirectoryHavePrivatePermissions()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        store.Accept(fixture.Envelope(8));
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(store.JournalPath).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            using var identity = WindowsIdentity.GetCurrent();
            var allowed = new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow)
                    Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, allowed);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.JournalPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(store.JournalPath)!));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "business-policy-" + Guid.NewGuid().ToString("N"));
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal ECDsa SigningKey => _key;
        internal BusinessPolicyStore Store(Action<string, string>? replaceFile = null, Action<string>? flushDirectory = null) =>
            new(BusinessPolicyConfigurationTests.Configuration(_key), Directory, replaceFile, flushDirectory);

        internal byte[] Envelope(long revision, bool applyNow = false, string action = "proxy",
            ECDsa? signingKey = null, string policyId = "fixture-policy", string keyId = "fixture-key")
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, policy_id = policyId, key_id = keyId, revision,
                published_at = 1700000000, apply = applyNow ? "now" : "next_connection",
                rules = new[] { new { domain = "example.com", action, comment = "Synthetic rule" } }
            });
            var signature = (signingKey ?? _key).SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature)
            });
        }

        public void Dispose()
        {
            _key.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
