using System.Security.Cryptography;
using System.Text;

namespace WindowsAxon.Mesh;

/// <summary>Derives domain-separated discovery and session keys from a shared P2P pair key.</summary>
internal sealed class PairKeyCrypto : IDisposable
{
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 100_000;

    private static readonly byte[] DomainSalt = Encoding.UTF8.GetBytes("WindowsAxon.Mesh.P2P.v1");
    private static readonly byte[] GroupIdInfo = Encoding.UTF8.GetBytes("group-id-v1");
    private static readonly byte[] DiscoveryAuthInfo = Encoding.UTF8.GetBytes("discovery-auth-v1");
    private static readonly byte[] SessionBaseInfo = Encoding.UTF8.GetBytes("session-key-v1");
    private static readonly byte[] ClientHelloLabel = Encoding.UTF8.GetBytes("c2s-hello");
    private static readonly byte[] ServerHelloLabel = Encoding.UTF8.GetBytes("s2c-hello");
    private static readonly byte[] ClientToServerKeyLabel = Encoding.UTF8.GetBytes("c2s-aes-gcm");
    private static readonly byte[] ServerToClientKeyLabel = Encoding.UTF8.GetBytes("s2c-aes-gcm");

    private readonly byte[] _discoveryAuthKey;
    private readonly byte[] _sessionBaseKey;

    public PairKeyCrypto(string pairKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairKey);

        var masterKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pairKey.Trim()),
                                                  DomainSalt,
                                                  Pbkdf2Iterations,
                                                  HashAlgorithmName.SHA256,
                                                  KeyBytes);
        try
        {
            var groupIdBytes = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 16, GroupIdInfo);
            GroupId = Convert.ToHexStringLower(groupIdBytes);
            _discoveryAuthKey = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, KeyBytes, DiscoveryAuthInfo);
            _sessionBaseKey = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, KeyBytes, SessionBaseInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public string GroupId { get; }

    public string ComputeBeaconMac(int version, string instanceId, string nodeName, int tcpPort, bool leaving)
    {
        var canonical = $"{version}|{GroupId}|{instanceId}|{nodeName}|{tcpPort}|{(leaving ? 1 : 0)}";
        var mac = HMACSHA256.HashData(_discoveryAuthKey, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(mac);
    }

    public bool VerifyBeaconMac(int version, string instanceId, string nodeName, int tcpPort, bool leaving, string? macBase64)
    {
        if (string.IsNullOrEmpty(macBase64))
        {
            return false;
        }

        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(macBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var canonical = $"{version}|{GroupId}|{instanceId}|{nodeName}|{tcpPort}|{(leaving ? 1 : 0)}";
        var expected = HMACSHA256.HashData(_discoveryAuthKey, Encoding.UTF8.GetBytes(canonical));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public byte[] ComputeClientHelloTag(ReadOnlySpan<byte> clientNonce)
    {
        var data = new byte[ClientHelloLabel.Length + clientNonce.Length];
        ClientHelloLabel.CopyTo(data, 0);
        clientNonce.CopyTo(data.AsSpan(ClientHelloLabel.Length));
        return HMACSHA256.HashData(_sessionBaseKey, data);
    }

    public byte[] ComputeServerHelloTag(ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        var data = new byte[ServerHelloLabel.Length + clientNonce.Length + serverNonce.Length];
        ServerHelloLabel.CopyTo(data, 0);
        clientNonce.CopyTo(data.AsSpan(ServerHelloLabel.Length));
        serverNonce.CopyTo(data.AsSpan(ServerHelloLabel.Length + clientNonce.Length));
        return HMACSHA256.HashData(_sessionBaseKey, data);
    }

    public (byte[] ClientToServerKey, byte[] ServerToClientKey) DeriveSessionKeys(ReadOnlySpan<byte> clientNonce,
                                                                                  ReadOnlySpan<byte> serverNonce)
    {
        var c2sInfo = new byte[ClientToServerKeyLabel.Length + clientNonce.Length + serverNonce.Length];
        ClientToServerKeyLabel.CopyTo(c2sInfo, 0);
        clientNonce.CopyTo(c2sInfo.AsSpan(ClientToServerKeyLabel.Length));
        serverNonce.CopyTo(c2sInfo.AsSpan(ClientToServerKeyLabel.Length + clientNonce.Length));

        var s2cInfo = new byte[ServerToClientKeyLabel.Length + clientNonce.Length + serverNonce.Length];
        ServerToClientKeyLabel.CopyTo(s2cInfo, 0);
        clientNonce.CopyTo(s2cInfo.AsSpan(ServerToClientKeyLabel.Length));
        serverNonce.CopyTo(s2cInfo.AsSpan(ServerToClientKeyLabel.Length + clientNonce.Length));

        var c2sKey = HKDF.Expand(HashAlgorithmName.SHA256, _sessionBaseKey, KeyBytes, c2sInfo);
        var s2cKey = HKDF.Expand(HashAlgorithmName.SHA256, _sessionBaseKey, KeyBytes, s2cInfo);
        return (c2sKey, s2cKey);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_discoveryAuthKey);
        CryptographicOperations.ZeroMemory(_sessionBaseKey);
    }
}
