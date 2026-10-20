using System.Runtime.InteropServices;
using System.Text;
using PromptForge.Core.Settings;
using PromptForge.Mac.Native;

namespace PromptForge.Mac.Infrastructure;

/// <summary>
/// API keys in the macOS login Keychain, one generic-password item per provider. The Keychain encrypts
/// them with your login password, and only PromptForge is on each item's access list, so other apps
/// trigger a macOS prompt before they can read a key. Nothing secret is written to settings.json or logs.
/// </summary>
internal sealed partial class KeychainSecretStore : ISecretStore
{
    private const string Service = "PromptForge API keys";
    private const string Lib = "/System/Library/Frameworks/Security.framework/Security";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private static readonly IntPtr Handle = NativeLibrary.Load(Lib);
    private static readonly IntPtr KSecClass = Constant("kSecClass");
    private static readonly IntPtr KSecClassGenericPassword = Constant("kSecClassGenericPassword");
    private static readonly IntPtr KSecAttrService = Constant("kSecAttrService");
    private static readonly IntPtr KSecAttrAccount = Constant("kSecAttrAccount");
    private static readonly IntPtr KSecAttrLabel = Constant("kSecAttrLabel");
    private static readonly IntPtr KSecValueData = Constant("kSecValueData");
    private static readonly IntPtr KSecReturnData = Constant("kSecReturnData");
    private static readonly IntPtr KSecMatchLimit = Constant("kSecMatchLimit");
    private static readonly IntPtr KSecMatchLimitOne = Constant("kSecMatchLimitOne");

    private readonly object _gate = new();

    [LibraryImport(Lib)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(Lib)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(Lib)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(Lib)]
    private static partial int SecItemDelete(IntPtr query);

    public string? Get(string providerId)
    {
        lock (_gate)
        {
            using var service = new CfString(Service);
            using var account = new CfString(providerId);
            var query = CF.CreateDictionary([
                (KSecClass, KSecClassGenericPassword), (KSecAttrService, service), (KSecAttrAccount, account),
                (KSecReturnData, CF.BooleanTrue), (KSecMatchLimit, KSecMatchLimitOne)]);
            try
            {
                var status = SecItemCopyMatching(query, out var data);
                if (status == ErrSecItemNotFound) return null;
                if (status != ErrSecSuccess)
                {
                    Log.Warn($"keychain read for {providerId} failed (OSStatus {status})");
                    return null;
                }
                try
                {
                    return Encoding.UTF8.GetString(CF.ToBytes(data));
                }
                finally
                {
                    CF.Release(data);
                }
            }
            finally
            {
                CF.Release(query);
            }
        }
    }

    public void Set(string providerId, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Delete(providerId);
                return;
            }

            using var service = new CfString(Service);
            using var account = new CfString(providerId);
            using var label = new CfString($"PromptForge: {providerId} API key");
            var secret = CF.CreateData(Encoding.UTF8.GetBytes(value.Trim()));
            var query = CF.CreateDictionary([(KSecClass, KSecClassGenericPassword), (KSecAttrService, service), (KSecAttrAccount, account)]);
            var update = CF.CreateDictionary([(KSecValueData, secret)]);
            var add = CF.CreateDictionary([
                (KSecClass, KSecClassGenericPassword), (KSecAttrService, service), (KSecAttrAccount, account), (KSecAttrLabel, label),
                (KSecValueData, secret)]);
            try
            {
                var status = SecItemAdd(add, IntPtr.Zero);
                if (status == ErrSecDuplicateItem) status = SecItemUpdate(query, update);
                if (status != ErrSecSuccess) throw new IOException($"The key couldn't be saved to the Keychain (OSStatus {status}).");
            }
            finally
            {
                CF.Release(add);
                CF.Release(update);
                CF.Release(query);
                CF.Release(secret);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            using var service = new CfString(Service);
            var query = CF.CreateDictionary([(KSecClass, KSecClassGenericPassword), (KSecAttrService, service)]);
            try
            {
                // Each call removes the matching items; repeat until none are left (bounded, in case of errors).
                for (var i = 0; i < 32 && SecItemDelete(query) == ErrSecSuccess; i++) { }
            }
            finally
            {
                CF.Release(query);
            }
        }
    }

    private static void Delete(string providerId)
    {
        using var service = new CfString(Service);
        using var account = new CfString(providerId);
        var query = CF.CreateDictionary([(KSecClass, KSecClassGenericPassword), (KSecAttrService, service), (KSecAttrAccount, account)]);
        try
        {
            var status = SecItemDelete(query);
            if (status is not (ErrSecSuccess or ErrSecItemNotFound)) Log.Warn($"keychain delete for {providerId} failed (OSStatus {status})");
        }
        finally
        {
            CF.Release(query);
        }
    }

    private static IntPtr Constant(string name) => CF.ReadConstant(Handle, name);

    /// <summary>A CFString released on dispose; converts to its pointer for dictionary building.</summary>
    private readonly struct CfString(string value) : IDisposable
    {
        private readonly IntPtr _ref = CF.CreateString(value);

        public static implicit operator IntPtr(CfString s) => s._ref;

        public void Dispose() => CF.Release(_ref);
    }
}
