using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Curs.Services;

// One credential per configured API, in the user's local login Keychain.
// Passwords and tokens never appear in process arguments or preference files.
public sealed class MacKeychainSessionStore(string apiAddress) : ISessionStore
{
    public SavedSession? Read()
    {
        using var query = Query();
        query.Add("kSecReturnData", Native.Constant(Native.Core, "kCFBooleanTrue"));
        var status = Native.SecItemCopyMatching(query.Handle, out var result);
        if (status == -25300) return null; // errSecItemNotFound
        Check(status);
        try
        {
            var length = checked((int)Native.CFDataGetLength(result));
            if (length is <= 0 or > 32768) { Clear(); return null; }
            var data = new byte[length]; Marshal.Copy(Native.CFDataGetBytePtr(result), data, 0, length);
            try
            {
                var saved = JsonSerializer.Deserialize<SavedSession>(data);
                if (string.IsNullOrWhiteSpace(saved?.AccessToken) || string.IsNullOrWhiteSpace(saved.Email)) { Clear(); return null; }
                return saved;
            }
            catch (JsonException) { Clear(); return null; }
        }
        finally { if (result != 0) Native.CFRelease(result); }
    }
    public void Write(SavedSession value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var query = Query();
        using var attributes = new DictionaryHandle();
        attributes.Data("kSecValueData", bytes);
        var status = Native.SecItemUpdate(query.Handle, attributes.Handle);
        if (status == -25300)
        {
            query.Data("kSecValueData", bytes);
            status = Native.SecItemAdd(query.Handle, 0);
        }
        Check(status);
    }
    public void Clear()
    {
        using var query = Query();
        var status = Native.SecItemDelete(query.Handle);
        if (status != -25300) Check(status);
    }
    private DictionaryHandle Query()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var query = new DictionaryHandle();
        query.Add("kSecClass", Native.Constant(Native.Security, "kSecClassGenericPassword"));
        query.Text("kSecAttrService", "com.kuum.kanbanboard.remembered-session");
        query.Text("kSecAttrAccount", apiAddress);
        return query;
    }
    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Keychain operation failed ({status}).");
    }
    private sealed class DictionaryHandle : IDisposable
    {
        public nint Handle { get; } = Native.CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(Native.Core, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(Native.Core, "kCFTypeDictionaryValueCallBacks"));
        public void Add(string key, nint value) => Native.CFDictionarySetValue(Handle, Native.Constant(Native.Security, key), value);
        public void Text(string key, string value)
        {
            var text = Native.CFStringCreateWithCString(0, value, 0x08000100);
            try { Add(key, text); } finally { Native.CFRelease(text); }
        }
        public void Data(string key, byte[] bytes)
        {
            var data = Native.CFDataCreate(0, bytes, bytes.Length);
            try { Add(key, data); } finally { Native.CFRelease(data); }
        }
        public void Dispose() => Native.CFRelease(Handle);
    }
    private static class Native
    {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string CorePath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal static readonly nint Security = NativeLibrary.Load(SecurityPath);
        internal static readonly nint Core = NativeLibrary.Load(CorePath);
        internal static nint Constant(nint library, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
        [DllImport(SecurityPath)] internal static extern int SecItemCopyMatching(nint query, out nint result);
        [DllImport(SecurityPath)] internal static extern int SecItemAdd(nint attributes, nint result);
        [DllImport(SecurityPath)] internal static extern int SecItemUpdate(nint query, nint attributes);
        [DllImport(SecurityPath)] internal static extern int SecItemDelete(nint query);
        [DllImport(CorePath)] internal static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
        [DllImport(CorePath)] internal static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
        [DllImport(CorePath)] internal static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
        [DllImport(CorePath)] internal static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
        [DllImport(CorePath)] internal static extern nint CFDataGetLength(nint data);
        [DllImport(CorePath)] internal static extern nint CFDataGetBytePtr(nint data);
        [DllImport(CorePath)] internal static extern void CFRelease(nint value);
    }
}
