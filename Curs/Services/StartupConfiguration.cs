using System;
using Curs.Services.LocalSetup;

namespace Curs.Services;

public static class StartupConfiguration
{
    public static Uri? ExplicitApiAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new SetupException("Адреса сервера в налаштуваннях некоректна. Потрібна повна адреса HTTP або HTTPS.", SetupAction.None);
        return uri;
    }
}
