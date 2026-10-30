using static ServiceLib.Handler.SysProxy.ProxySettingWindows.InternetConnectionOption;

namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("windows")]
public static class ProxySettingWindows
{
    private const string _regPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static void SetProxyFallback(string? strProxy, string? exceptions, int type)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_regPath, true)
            ?? throw new IOException("无法写入 Windows 系统代理设置。");
        key.SetValue("ProxyEnable", type == 2 ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("ProxyServer", type == 2 ? strProxy ?? "" : "");
        key.SetValue("ProxyOverride", type == 2 ? exceptions ?? "" : "");
        key.SetValue("AutoConfigURL", type == 4 ? strProxy ?? "" : "");
        key.Flush();
    }

    private static readonly object ProxyWriteGate = new();

    public static int ReadConnectionFlags() => ReadConnectionProxy().Flags;

    public static (int Flags, WindowsProxyEndpoint Endpoint) ReadConnectionProxy()
    {
        var size = Marshal.SizeOf<InternetConnectionOption>();
        var pointer = Marshal.AllocHGlobal(size * 4);
        var list = new InternetPerConnOptionList { dwSize = Marshal.SizeOf<InternetPerConnOptionList>(), dwOptionCount = 4, options = pointer };
        var listPointer = Marshal.AllocHGlobal(list.dwSize);
        var queried = false;
        try
        {
            for (var i = 0; i < 4; i++)
                Marshal.StructureToPtr(new InternetConnectionOption { m_Option = (PerConnOption)(i + 1) }, pointer + i * size, false);
            Marshal.StructureToPtr(list, listPointer, false);
            var length = list.dwSize;
            if (!NativeMethods.InternetQueryOption(nint.Zero, InternetOption.INTERNET_OPTION_PER_CONNECTION_OPTION, listPointer, ref length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "读取 WinINet 代理失败");
            queried = true;
            var flags = Marshal.PtrToStructure<InternetConnectionOption>(pointer).m_Value.m_Int;
            string Text(int i) => Marshal.PtrToStringUni(Marshal.PtrToStructure<InternetConnectionOption>(pointer + i * size).m_Value.m_StringPtr) ?? "";
            return (flags, new((flags & 2) != 0, (flags & 8) != 0, Text(1), Text(2), Text(3)));
        }
        finally
        {
            if (queried)
                for (var i = 1; i < 4; i++)
                    NativeMethods.GlobalFree(Marshal.PtrToStructure<InternetConnectionOption>(pointer + i * size).m_Value.m_StringPtr);
            Marshal.FreeHGlobal(listPointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static WindowsProxyEndpoint ReadWinHttpProxy()
    {
        if (!NativeMethods.WinHttpGetIEProxyConfigForCurrentUser(out var config))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "读取 WinHTTP 当前用户代理失败");
        try
        {
            var server = Marshal.PtrToStringUni(config.Proxy) ?? "";
            return new(server.Length > 0, config.AutoDetect, server,
                Marshal.PtrToStringUni(config.Bypass) ?? "", Marshal.PtrToStringUni(config.Pac) ?? "");
        }
        finally
        {
            NativeMethods.GlobalFree(config.Proxy);
            NativeMethods.GlobalFree(config.Bypass);
            NativeMethods.GlobalFree(config.Pac);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinHttpProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect;
        public nint Pac;
        public nint Proxy;
        public nint Bypass;
    }

    /// <summary>
    // set to use no proxy
    /// </summary>
    /// <exception cref="ApplicationException">Error message with win32 error code</exception>
    public static bool UnsetProxy()
    {
        return SetProxy(null, null, 1);
    }

    /// <summary>
    /// Set system proxy settings
    /// </summary>
    /// <param name="strProxy"> proxy address</param>
    /// <param name="exceptions">exception addresses that do not use proxy</param>
    /// <param name="type">type of proxy defined in PerConnFlags
    ///     PROXY_TYPE_DIRECT           = 0x00000001, // direct connection (no proxy)
    ///     PROXY_TYPE_PROXY            = 0x00000002, // via named proxy
    ///     PROXY_TYPE_AUTO_PROXY_URL   = 0x00000004, // autoproxy script URL
    ///     PROXY_TYPE_AUTO_DETECT      = 0x00000008  // use autoproxy detection
    /// </param>
    /// <exception cref="ApplicationException">Error message with win32 error code</exception>
    /// <returns>true: one of connection is successfully updated proxy settings</returns>
    public static bool SetProxy(string? strProxy, string? exceptions, int type)
    {
        if (type is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(type));
        if (type != 1 && string.IsNullOrWhiteSpace(strProxy)) throw new ArgumentException("代理地址不能为空", nameof(strProxy));
        lock (ProxyWriteGate)
        {
            Logging.SaveLog($"[系统代理] 请求 type={type}；操作前：{WindowsProxySnapshot.Read().Detail}");
            // RAS first; LAN and the public registry are finalized together before notifying clients.
            foreach (var connection in EnumerateRasEntries()) SetConnectionProxy(connection, strProxy, exceptions, type);
            WindowsProxySnapshot actual = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                SetConnectionProxy(null, strProxy, exceptions, type);
                SetProxyFallback(strProxy, exceptions, type);
                NotifyProxyChanged();
                actual = WindowsProxySnapshot.Read();
                if (actual.Matches(type, strProxy, exceptions))
                {
                    // Catch immediate overwrites; never fight another proxy app with a background writer.
                    Thread.Sleep(100);
                    actual = WindowsProxySnapshot.Read();
                    if (actual.Matches(type, strProxy, exceptions))
                    {
                        Logging.SaveLog($"[系统代理] 已验证 type={type}；{actual.Detail}");
                        return true;
                    }
                }
                Logging.SaveLog($"[系统代理] 回读不一致 attempt={attempt + 1}；{actual.Detail}");
            }
            throw new IOException("系统代理未完整生效，请检查其他代理程序或系统策略。" + actual?.Detail);
        }
    }

    private static void NotifyProxyChanged()
    {
        var changed = NativeMethods.InternetSetOption(nint.Zero, InternetOption.INTERNET_OPTION_SETTINGS_CHANGED, nint.Zero, 0);
        var changedError = changed ? 0 : Marshal.GetLastWin32Error();
        var refreshed = NativeMethods.InternetSetOption(nint.Zero, InternetOption.INTERNET_OPTION_REFRESH, nint.Zero, 0);
        if (!changed || !refreshed)
            throw new System.ComponentModel.Win32Exception(!changed ? changedError : Marshal.GetLastWin32Error(), "代理已写入，但通知 Windows 刷新失败");
    }

    private static bool SetConnectionProxy(string? connectionName, string? strProxy, string? exceptions, int type)
    {
        var options = new InternetConnectionOption[4];
        var optionsPointer = nint.Zero;
        var listPointer = nint.Zero;
        var connection = nint.Zero;
        try
        {
            options[0].m_Option = PerConnOption.INTERNET_PER_CONN_FLAGS;
            options[0].m_Value.m_Int = type == 2 ? 3 : type == 4 ? 5 : 1;
            options[1].m_Option = PerConnOption.INTERNET_PER_CONN_PROXY_SERVER;
            options[1].m_Value.m_StringPtr = Marshal.StringToHGlobalUni(type == 2 ? strProxy ?? "" : "");
            options[2].m_Option = PerConnOption.INTERNET_PER_CONN_PROXY_BYPASS;
            options[2].m_Value.m_StringPtr = Marshal.StringToHGlobalUni(type == 2 ? exceptions ?? "" : "");
            options[3].m_Option = PerConnOption.INTERNET_PER_CONN_AUTOCONFIG_URL;
            options[3].m_Value.m_StringPtr = Marshal.StringToHGlobalUni(type == 4 ? strProxy ?? "" : "");
            if (connectionName != null) connection = Marshal.StringToHGlobalUni(connectionName);
            var size = Marshal.SizeOf<InternetConnectionOption>();
            optionsPointer = Marshal.AllocHGlobal(size * options.Length);
            for (var i = 0; i < options.Length; i++) Marshal.StructureToPtr(options[i], optionsPointer + i * size, false);
            var list = new InternetPerConnOptionList
            {
                dwSize = Marshal.SizeOf<InternetPerConnOptionList>(), szConnection = connection,
                dwOptionCount = options.Length, options = optionsPointer
            };
            listPointer = Marshal.AllocHGlobal(list.dwSize);
            Marshal.StructureToPtr(list, listPointer, false);
            if (!NativeMethods.InternetSetOption(nint.Zero, InternetOption.INTERNET_OPTION_PER_CONNECTION_OPTION, listPointer, list.dwSize))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "写入 Windows 连接代理失败");
            // Notify once, only after LAN, RAS and the public registry have all been written.
            return true;
        }
        finally
        {
            for (var i = 1; i < options.Length; i++) Marshal.FreeHGlobal(options[i].m_Value.m_StringPtr);
            Marshal.FreeHGlobal(connection);
            Marshal.FreeHGlobal(optionsPointer);
            Marshal.FreeHGlobal(listPointer);
        }
    }

    /// <summary>
    /// Retrieve list of connections including LAN and WAN to support PPPoE connection
    /// </summary>
    /// <returns>A list of RAS connection names. May be empty list if no dial up connection.</returns>
    /// <exception cref="ApplicationException">Error message with win32 error code</exception>
    private static IEnumerable<string> EnumerateRasEntries()
    {
        var entries = 0;
        // attempt to query with 1 entry buffer
        var rasEntryNames = new RASENTRYNAME[1];
        var bufferSize = Marshal.SizeOf(typeof(RASENTRYNAME));
        rasEntryNames[0].dwSize = Marshal.SizeOf(typeof(RASENTRYNAME));

        var result = NativeMethods.RasEnumEntries(null, null, rasEntryNames, ref bufferSize, ref entries);
        // increase buffer if the buffer is not large enough
        if (result == (uint)ErrorCode.ERROR_BUFFER_TOO_SMALL)
        {
            rasEntryNames = new RASENTRYNAME[bufferSize / Marshal.SizeOf(typeof(RASENTRYNAME))];
            for (var i = 0; i < rasEntryNames.Length; i++)
            {
                rasEntryNames[i].dwSize = Marshal.SizeOf(typeof(RASENTRYNAME));
            }

            result = NativeMethods.RasEnumEntries(null, null, rasEntryNames, ref bufferSize, ref entries);
        }
        if (result == 0)
        {
            var entryNames = new List<string>();
            for (var i = 0; i < entries; i++)
            {
                entryNames.Add(rasEntryNames[i].szEntryName);
            }

            return entryNames;
        }
        throw new ApplicationException($"RasEnumEntries failed with error code: {result}");
    }

    #region WinInet structures

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct InternetPerConnOptionList
    {
        public int dwSize;               // size of the INTERNET_PER_CONN_OPTION_LIST struct
        public nint szConnection;         // connection name to set/query options
        public int dwOptionCount;        // number of options to set/query
        public int dwOptionError;           // on error, which option failed

        //[MarshalAs(UnmanagedType.)]
        public nint options;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct InternetConnectionOption
    {
        private static readonly int Size;
        public PerConnOption m_Option;
        public InternetConnectionOptionValue m_Value;

        static InternetConnectionOption()
        {
            Size = Marshal.SizeOf(typeof(InternetConnectionOption));
        }

        // Nested Types
        [StructLayout(LayoutKind.Explicit)]
        public struct InternetConnectionOptionValue
        {
            // Fields
            [FieldOffset(0)]
            public System.Runtime.InteropServices.ComTypes.FILETIME m_FileTime;

            [FieldOffset(0)]
            public int m_Int;

            [FieldOffset(0)]
            public nint m_StringPtr;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct RASENTRYNAME
        {
            public int dwSize;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxEntryName + 1)]
            public string szEntryName;

            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH + 1)]
            public string szPhonebookPath;
        }

        // Constants
        public const int RAS_MaxEntryName = 256;

        public const int MAX_PATH = 260; // Standard MAX_PATH value in Windows
    }

    #endregion WinInet structures

    #region WinInet enums

    //
    // options manifests for Internet{Query|Set}Option
    //
    public enum InternetOption : uint
    {
        INTERNET_OPTION_PER_CONNECTION_OPTION = 75,
        INTERNET_OPTION_REFRESH = 37,
        INTERNET_OPTION_SETTINGS_CHANGED = 39
    }

    //
    // Options used in INTERNET_PER_CONN_OPTON struct
    //
    public enum PerConnOption
    {
        INTERNET_PER_CONN_FLAGS = 1, // Sets or retrieves the connection type. The Value member will contain one or more of the values from PerConnFlags
        INTERNET_PER_CONN_PROXY_SERVER = 2, // Sets or retrieves a string containing the proxy servers.
        INTERNET_PER_CONN_PROXY_BYPASS = 3, // Sets or retrieves a string containing the URLs that do not use the proxy server.
        INTERNET_PER_CONN_AUTOCONFIG_URL = 4//, // Sets or retrieves a string containing the URL to the automatic configuration script.
    }

    //
    // PER_CONN_FLAGS
    //
    [Flags]
    public enum PerConnFlags
    {
        PROXY_TYPE_DIRECT = 0x00000001,  // direct to net
        PROXY_TYPE_PROXY = 0x00000002,  // via named proxy
        PROXY_TYPE_AUTO_PROXY_URL = 0x00000004,  // autoproxy URL
        PROXY_TYPE_AUTO_DETECT = 0x00000008   // use autoproxy detection
    }

    public enum ErrorCode : uint
    {
        ERROR_BUFFER_TOO_SMALL = 603,
        ERROR_INVALID_SIZE = 632
    }

    #endregion WinInet enums

    internal static class NativeMethods
    {
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpGetIEProxyConfigForCurrentUser(out WinHttpProxyConfig config);

        [DllImport("kernel32.dll")]
        internal static extern nint GlobalFree(nint memory);

        [DllImport("WinInet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetQueryOption(nint hInternet, InternetOption option, nint buffer, ref int size);

        [DllImport("WinInet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetSetOption(nint hInternet, InternetOption dwOption, nint lpBuffer, int dwBufferLength);

        [DllImport("Rasapi32.dll", CharSet = CharSet.Auto)]
        public static extern uint RasEnumEntries(
            string? reserved,          // Reserved, must be null
            string? lpszPhonebook,     // Pointer to full path and filename of phone-book file. If this parameter is NULL, the entries are enumerated from all the remote access phone-book files
            [In, Out] RASENTRYNAME[]? lprasentryname, // Buffer to receive RAS entry names
            ref int lpcb,             // Size of the buffer
            ref int lpcEntries        // Number of entries written to the buffer
        );
    }
}
