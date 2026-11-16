using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal sealed class BusinessPolicyStore
{
    private const int HeaderBytes = 84;
    private static readonly byte[] Magic = "VRPOL001"u8.ToArray();
    private readonly BusinessPolicyConfiguration _configuration;
    private readonly bool _telephony;
    private readonly string _trustId;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _lockPath;
    private readonly Action<string, string> _replaceFile;
    private readonly Action<string> _flushDirectory;
    private BusinessPolicySnapshot? _current;

    internal string JournalPath { get; }
    internal BusinessPolicySnapshot? Current => Volatile.Read(ref _current);

    internal BusinessPolicyStore(BusinessPolicyConfiguration configuration, string dataDirectory,
        Action<string, string>? replaceFile = null, Action<string>? flushDirectory = null,
        bool telephony = false)
    {
        _configuration = configuration;
        _telephony = telephony;
        _trustId = telephony ? Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configuration.TrustId + ":telephony:v1"))) : configuration.TrustId;
        _directory = Path.GetFullPath(Path.Combine(dataDirectory, "policy"));
        JournalPath = Path.Combine(_directory, _trustId + ".bin");
        _lockPath = Path.Combine(_directory, _trustId + ".lock");
        _replaceFile = replaceFile ?? BusinessPolicyDurability.Replace;
        _flushDirectory = flushDirectory ?? BusinessPolicyDurability.FlushDirectory;
    }

    internal BusinessPolicySnapshot? Load()
    {
        lock (_gate)
        {
            try
            {
                using var lease = AcquireLease();
                var snapshot = ReadJournal();
                RequireUnchangedFloor(snapshot, _current);
                if (snapshot != null) _flushDirectory(_directory);
                Volatile.Write(ref _current, snapshot);
                return snapshot;
            }
            catch (Exception e) when (IsStorageFailure(e)) { throw Failure(); }
        }
    }

    internal BusinessPolicySnapshot Accept(byte[] envelope)
    {
        // The caller retains its array; it must not be able to change bytes after verification.
        if (envelope.Length is 0 or > BusinessPolicy.MaximumResponseBytes) throw Failure();
        var captured = envelope.ToArray();
        var candidate = Parse(captured);
        lock (_gate)
        {
            try
            {
                using var lease = AcquireLease();
                var saved = ReadJournal();
                RequireUnchangedFloor(saved, _current);
                RequireUnchangedFloor(candidate, saved);
                if (saved != null && saved.Revision == candidate.Revision)
                {
                    _flushDirectory(_directory);
                    Volatile.Write(ref _current, saved);
                    return saved;
                }
                Persist(candidate, captured);
                Volatile.Write(ref _current, candidate);
                return candidate;
            }
            catch (Exception e) when (IsStorageFailure(e)) { throw Failure(); }
        }
    }

    private FileStream AcquireLease()
    {
        RefuseLinks(_directory);
        BusinessPrivateFiles.CreateDirectory(_directory);
        // Persist creation of the policy directory before accepting its first file.
        _flushDirectory(Path.GetDirectoryName(_directory)!);
        RefuseLinks(_lockPath);
        // The lock inode is never deleted/replaced. Contention fails safely for a later poll.
        var lease = AppPaths.CreatePrivateFile(_lockPath, FileMode.OpenOrCreate);
        try
        {
            ProtectFile(_lockPath);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private BusinessPolicySnapshot? ReadJournal()
    {
        RefuseLinks(JournalPath);
        FileStream input;
        try { input = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= HeaderBytes or > HeaderBytes + BusinessPolicy.MaximumResponseBytes)
                throw Failure();
            ProtectFile(JournalPath);
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || !bytes.AsSpan(0, 8).SequenceEqual(Magic) ||
                !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(8, 32), Convert.FromHexString(_trustId)))
                throw Failure();
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(80, 4));
            if (length != bytes.Length - HeaderBytes) throw Failure();
            var snapshot = Parse(bytes.AsMemory(HeaderBytes));
            if (snapshot.Revision != BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(40, 8)) ||
                !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(48, 32), Convert.FromHexString(snapshot.PayloadDigest)))
                throw Failure();
            return snapshot;
        }
    }

    private void Persist(BusinessPolicySnapshot candidate, byte[] envelope)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        Convert.FromHexString(_trustId).CopyTo(header, 8);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(40, 8), candidate.Revision);
        Convert.FromHexString(candidate.PayloadDigest).CopyTo(header, 48);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(80, 4), envelope.Length);
        var temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = AppPaths.CreatePrivateFile(temporary, FileMode.CreateNew))
            {
                output.Write(header);
                output.Write(envelope);
                output.Flush(flushToDisk: true);
            }
            ProtectFile(temporary);
            RefuseLinks(JournalPath);
            _replaceFile(temporary, JournalPath);
            _flushDirectory(_directory);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void RequireUnchangedFloor(BusinessPolicySnapshot? candidate, BusinessPolicySnapshot? floor)
    {
        if (floor != null && (candidate == null || candidate.Revision < floor.Revision ||
            (candidate.Revision == floor.Revision && candidate.PayloadDigest != floor.PayloadDigest)))
            throw Failure();
    }

    private BusinessPolicySnapshot Parse(ReadOnlyMemory<byte> envelope)
    {
        if (!_telephony) return BusinessPolicy.Parse(envelope, _configuration.PolicyId, _configuration.Keys);
        var phone = BusinessPhonePolicy.Parse(envelope, _configuration.PolicyId, _configuration.Keys);
        if (phone.Rules.Any(rule => rule.Type == "ip_cidr" && rule.Value.Contains(':')))
            throw Failure();
        // Reuse the durable journal machinery; this container never becomes a site policy.
        return new(phone.PolicyId, phone.KeyId, phone.Revision, phone.PublishedAt,
            phone.ApplyNow, [], phone.PayloadDigest, phone);
    }

    private static void RefuseLinks(string path)
    {
        BusinessPrivateFiles.RefuseLinks(path);
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((Path.GetDirectoryName(item) == null ? (FileSystemInfo)new DirectoryInfo(item) : new FileInfo(item)).LinkTarget != null)
                throw Failure();
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or CryptographicException or NotSupportedException;

    private static InvalidDataException Failure() => new("Business policy storage is unavailable or invalid.");
}
