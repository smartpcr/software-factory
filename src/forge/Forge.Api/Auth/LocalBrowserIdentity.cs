// -----------------------------------------------------------------------
// <copyright file="LocalBrowserIdentity.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public interface ILocalBrowserIdentity
{
    bool IsHostUser(HttpContext context);
}

/// <summary>
/// Verifies the owner of the actual client TCP socket, never a claimed username.
/// The browser must connect directly: a development proxy is a different peer.
/// </summary>
public sealed class LocalBrowserIdentity : ILocalBrowserIdentity
{
    public bool IsHostUser(HttpContext context)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var pid = FindPeerProcess(context);
            if (pid is null or <= 0) return false;
            using var process = OpenProcess(0x1000, false, pid.Value); // QUERY_LIMITED_INFORMATION
            if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return false;
            using (token)
            using (var peer = new WindowsIdentity(token.DangerousGetHandle()))
            using (var host = WindowsIdentity.GetCurrent())
            {
                // Recheck while retaining the process handle to exclude a stale socket/PID.
                return peer.User is not null && peer.User == host.User && FindPeerProcess(context) == pid;
            }
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static int? FindPeerProcess(HttpContext context)
    {
        var client = Normalize(context.Connection.RemoteIpAddress);
        var server = Normalize(context.Connection.LocalIpAddress);
        if (client is null || server is null || !IPAddress.IsLoopback(client) || !IPAddress.IsLoopback(server)
            || client.AddressFamily != server.AddressFamily || context.Connection.RemotePort <= 0 || context.Connection.LocalPort <= 0)
            return null;
        var v6 = client.AddressFamily == AddressFamily.InterNetworkV6;
        var family = v6 ? 23 : 2;
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0) != 122) return null;
        // The table may grow between calls. Retry only buffer-size changes.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) return null;
            var allocated = size;
            var table = Marshal.AllocHGlobal(allocated);
            try
            {
                var result = GetExtendedTcpTable(table, ref size, false, family, 5, 0);
                if (result == 122) continue;
                if (result != 0) return null;
                var count = Marshal.ReadInt32(table);
                var stride = v6 ? 56 : 24;
                if (count < 0 || count > (allocated - 4) / stride) return null;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(table, 4 + i * stride);
                    if (Marshal.ReadInt32(row, v6 ? 48 : 0) != 5 // ESTABLISHED
                        || Port(row, v6 ? 20 : 8) != context.Connection.RemotePort
                        || Port(row, v6 ? 44 : 16) != context.Connection.LocalPort
                        || !Address(row, v6 ? 0 : 4, v6).Equals(client)
                        || !Address(row, v6 ? 24 : 12, v6).Equals(server)) continue;
                    return Marshal.ReadInt32(row, v6 ? 52 : 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return null;
    }

    private static IPAddress? Normalize(IPAddress? ip) => ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip;
    private static int Port(IntPtr row, int offset)
    {
        var value = Marshal.ReadInt32(row, offset);
        return ((value & 255) << 8) | ((value >> 8) & 255);
    }

    private static IPAddress Address(IntPtr row, int offset, bool v6)
    {
        var bytes = new byte[v6 ? 16 : 4];
        Marshal.Copy(IntPtr.Add(row, offset), bytes, 0, bytes.Length);
        return new IPAddress(bytes); // Supported loopback IPv6 has scope zero.
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted,
        int addressFamily, int tableClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, int access, out SafeAccessTokenHandle token);
}
