using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seein.SmartHome;

public sealed record SmartHomeSettings(string RouterAddress = "", string LampAddress = "", string Region = "cn");

public static class LocalDeviceAddress
{
    public static IPAddress Parse(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("请输入设备的局域网 IPv4 地址。");
        var b = ip.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31))
            throw new ArgumentException("设备地址必须在家庭局域网内。");
        return ip;
    }

    public static Uri Router(string address)
    {
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请输入路由器管理地址，例如 http://192.168.1.1/。");
        Parse(uri.Host);
        return uri;
    }

    public static bool SameOrigin(Uri origin, string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && origin.Scheme == uri.Scheme && origin.Host == uri.Host && origin.Port == uri.Port && uri.UserInfo.Length == 0;
}

public sealed class LampAuthorization(string name, string model, string address, string token)
{
    public string Name { get; } = name;
    public string Model { get; } = model;
    public string Address { get; } = address;
    public string Token { get; } = token;
    public override string ToString() => Name + " · " + Address;
}

public sealed class SmartHomeStore(string userDirectory)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SEEIN.SmartHome.Lamp.v1");
    private string DirectoryPath => Path.Combine(userDirectory, "SmartHome");
    private string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private string CredentialPath => Path.Combine(DirectoryPath, "lamp.authorization");
    private string RouterCredentialPath => Path.Combine(DirectoryPath, "router.authorization");
    private string LampProfilePath => Path.Combine(DirectoryPath, "lamp-controls.json");

    public LampControlProfile LoadLampProfile()
    {
        if (!File.Exists(LampProfilePath)) return new();
        var profile = JsonSerializer.Deserialize<LampControlProfile>(File.ReadAllText(LampProfilePath)) ?? new();
        profile.Validate();
        return profile;
    }

    public void SaveLampProfile(LampControlProfile profile)
    {
        profile.Validate();
        SaveAtomic(LampProfilePath, JsonSerializer.SerializeToUtf8Bytes(profile));
    }

    public RouterCredential? LoadRouterCredential(Uri origin)
    {
        if (!File.Exists(RouterCredentialPath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(RouterCredentialPath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var credential = JsonSerializer.Deserialize<RouterCredential>(plain);
            return credential is not null && LocalDeviceAddress.SameOrigin(origin, credential.Origin) ? credential : null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void SaveRouterCredential(Uri origin, string password)
    {
        LocalDeviceAddress.Router(origin.AbsoluteUri);
        if (string.IsNullOrEmpty(password) || password.Length > 256) throw new ArgumentException("路由器密码长度无效。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new RouterCredential(origin.AbsoluteUri, password));
        try { SaveAtomic(RouterCredentialPath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public SmartHomeSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<SmartHomeSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void SaveSettings(SmartHomeSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RouterAddress)) LocalDeviceAddress.Router(settings.RouterAddress);
        if (!string.IsNullOrWhiteSpace(settings.LampAddress)) LocalDeviceAddress.Parse(settings.LampAddress);
        SaveAtomic(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings));
    }

    public LampAuthorization? LoadAuthorization()
    {
        if (!File.Exists(CredentialPath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(CredentialPath), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<LampAuthorization>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void SaveAuthorization(LampAuthorization authorization)
    {
        MiioPacket.ParseToken(authorization.Token);
        var plain = JsonSerializer.SerializeToUtf8Bytes(authorization);
        try { SaveAtomic(CredentialPath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void ForgetAuthorization() { if (File.Exists(CredentialPath)) File.Delete(CredentialPath); }

    private static void SaveAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
