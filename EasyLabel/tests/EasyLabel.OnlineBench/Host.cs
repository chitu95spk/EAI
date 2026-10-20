using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EasyLabel.OnlineBench;

internal sealed record Affinity(string Requested, int LogicalCpus, string Mask, string Priority);

internal static class Host
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr process, nuint processMask);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    public static Affinity ApplyAffinity(string requested)
    {
        using Process process = Process.GetCurrentProcess();
        if (!GetProcessAffinityMask(process.Handle, out nuint allowed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int available = System.Numerics.BitOperations.PopCount((ulong)allowed);
        int count = requested == "all" ? available : int.Parse(requested, System.Globalization.CultureInfo.InvariantCulture);
        if (count > available) throw new NotSupportedException($"Requested {count} logical CPUs; only {available} allowed in the current Windows processor group.");
        nuint selected = 0;
        for (int bit = 0, chosen = 0; bit < IntPtr.Size * 8 && chosen < count; bit++)
            if ((allowed & ((nuint)1 << bit)) != 0) { selected |= (nuint)1 << bit; chosen++; }
        if (!SetProcessAffinityMask(process.Handle, selected)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessAffinityMask(process.Handle, out nuint actual, out _) || actual != selected)
            throw new InvalidOperationException("Affinity read-back did not match the requested mask.");
        return new(requested, count, "0x" + ((ulong)actual).ToString("X"), process.PriorityClass.ToString());
    }

    public static Dictionary<string, string> Machine(string root)
    {
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var ram = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool memory = GlobalMemoryStatusEx(ref ram);
        using var process = Process.GetCurrentProcess();
        return new()
        {
            ["CPU model"] = cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unknown",
            ["Logical cores (OS)"] = OSLogicalCount().ToString(),
            ["Physical cores (OS)"] = PhysicalCores(),
            ["RAM bytes"] = memory ? ram.TotalPhysical.ToString() : "unknown",
            ["OS"] = RuntimeInformation.OSDescription,
            [".NET"] = RuntimeInformation.FrameworkDescription,
            ["Process architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["Process priority"] = process.PriorityClass.ToString(),
            ["Commit (HEAD only; working tree not inspected)"] = ReadHead(root),
            ["Stopwatch frequency Hz"] = Stopwatch.Frequency.ToString(),
            ["Stopwatch high resolution"] = Stopwatch.IsHighResolution.ToString()
        };
    }

    private static int OSLogicalCount()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
        int count = key?.SubKeyCount ?? 0;
        return count == 0 ? Environment.ProcessorCount : count;
    }

    private static string PhysicalCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length); // RelationProcessorCore
        if (length == 0) return "unknown";
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return "unknown";
            int count = 0;
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 8 || (long)offset + size > length) return "unknown";
                count++;
                offset += size;
            }
            return count.ToString();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadHead(string root)
    {
        try
        {
            string git = Path.Combine(root, ".git");
            if (File.Exists(git)) git = Path.GetFullPath(File.ReadAllText(git).Trim()[8..], root);
            string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
            string reference = head[5..];
            if (File.Exists(Path.Combine(git, "commondir"))) git = Path.GetFullPath(File.ReadAllText(Path.Combine(git, "commondir")).Trim(), git);
            string loose = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim();
            string packed = Path.Combine(git, "packed-refs");
            return File.Exists(packed) ? File.ReadLines(packed).FirstOrDefault(l => l.EndsWith(" " + reference, StringComparison.Ordinal))?.Split(' ')[0] ?? "unknown" : "unknown";
        }
        catch (Exception error) { return "unavailable: " + error.Message; }
    }
}

internal sealed class EchoAck : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TcpClient _client;
    private readonly Task _server;
    private readonly CancellationTokenSource _cancel = new();
    private readonly NetworkStream _stream;

    public EchoAck()
    {
        _listener.Start(1);
        _server = Task.Factory.StartNew(Serve, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _client = new TcpClient { NoDelay = true, ReceiveTimeout = 30000, SendTimeout = 30000 };
        _client.Connect((IPEndPoint)_listener.LocalEndpoint);
        _stream = _client.GetStream();
    }

    private void Serve()
    {
        try
        {
            using TcpClient peer = _listener.AcceptTcpClient();
            peer.NoDelay = true;
            peer.ReceiveTimeout = peer.SendTimeout = 30000;
            using NetworkStream stream = peer.GetStream();
            byte[] header = new byte[8], buffer = new byte[81920];
            while (!_cancel.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                long remaining = BinaryPrimitives.ReadInt64LittleEndian(header);
                if (remaining < 0 || remaining > 128 * 1024 * 1024) throw new InvalidDataException("Invalid payload length.");
                while (remaining > 0)
                {
                    int received = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (received == 0) throw new EndOfStreamException();
                    remaining -= received;
                }
                stream.Write(header); // Ack echoes the exact received byte count, not the payload.
            }
        }
        catch (Exception error) when (_cancel.IsCancellationRequested && error is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Send(byte[] bytes)
    {
        Span<byte> header = stackalloc byte[8], ack = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(header, bytes.LongLength);
        _stream.Write(header);
        _stream.Write(bytes);
        _stream.ReadExactly(ack);
        if (!header.SequenceEqual(ack)) throw new InvalidDataException("Loopback acknowledgement did not match the payload byte count.");
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _client.Dispose();
        _listener.Stop();
        try { _server.GetAwaiter().GetResult(); }
        finally { _cancel.Dispose(); }
    }
}
