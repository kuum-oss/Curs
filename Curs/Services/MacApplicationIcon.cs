using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Curs.Services;

internal static class MacApplicationIcon
{
    // Window.Icon is a no-op on macOS. Set the running application's Dock icon
    // as well, so Rider/dotnet launches have the same identity as the .app bundle.
    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var resource = AssetLoader.Open(new Uri("avares://Curs/Assets/kanbanboard.png"));
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        nint data = 0, image = 0;
        try
        {
            data = SendBytes(Send(GetClass("NSData"), Selector("alloc")),
                Selector("initWithBytes:length:"), pinned.AddrOfPinnedObject(), (nuint)bytes.Length);
            image = SendObject(Send(GetClass("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
            if (image == 0) throw new InvalidOperationException("Cannot decode the KanbanBoard Dock icon.");
            var application = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            SendVoid(application, Selector("setApplicationIconImage:"), image);
        }
        finally
        {
            if (image != 0) Release(image, Selector("release"));
            if (data != 0) Release(data, Selector("release"));
            pinned.Free();
        }
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendObject(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendBytes(nint receiver, nint selector, nint bytes, nuint length);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Release(nint receiver, nint selector);
}
