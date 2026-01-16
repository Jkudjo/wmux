using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WinMux.Core.Protocol;
using WinMux.Daemon.Interop;

namespace WinMux.Daemon.Sessions;

public sealed class SessionManager
{
    private readonly ILogger<SessionManager> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _nameToId = new();
    private readonly WinMux.Daemon.Configuration.WinMuxConfig _config;

    public SessionManager(ILoggerFactory loggerFactory, WinMux.Daemon.Configuration.WinMuxConfig config)
    {
        _logger = loggerFactory.CreateLogger<SessionManager>();
        _config = config;
    }

    public IReadOnlyList<SessionSummary> List()
    {
        return _sessions.Values.Select(s => s.ToSummary()).OrderBy(s => s.CreatedAt).ToList();
    }

    public Session? GetByIdOrName(string idOrName)
    {
        if (_sessions.TryGetValue(idOrName, out var s)) return s;
        if (_nameToId.TryGetValue(idOrName, out var id) && _sessions.TryGetValue(id, out s)) return s;
        return null;
    }

    public (Session session, CreatedEvent created) Create(CreateSessionRequest req, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = string.IsNullOrWhiteSpace(req.Name) ? id[..6] : req.Name;
        var cols = req.Cols ?? 120;
        var rows = req.Rows ?? 30;
        var shell = string.IsNullOrWhiteSpace(req.Shell) ? DefaultShell() : req.Shell!;
        var cwd = string.IsNullOrWhiteSpace(req.Cwd) ? DefaultCwd() : req.Cwd!;

        var session = new Session(_logger, id, name, shell, cwd, cols, rows);
        session.Start();
        _sessions[id] = session;
        _nameToId[name] = id;
        return (session, new CreatedEvent(id));
    }

    public void Remove(Session s)
    {
        _sessions.TryRemove(s.Id, out _);
        _nameToId.TryRemove(s.Name, out _);
    }

    private string DefaultShell()
    {
        return Environment.ExpandEnvironmentVariables(_config.DefaultShell);
    }

    private string DefaultCwd()
    {
        return Environment.ExpandEnvironmentVariables(_config.DefaultCwd);
    }
}

public sealed class Session
{
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private readonly List<Action<byte[]>> _listeners = new();

    public string Id { get; }
    public string Name { get; }
    public string Shell { get; }
    public string Cwd { get; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ShellPid { get; private set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; private set; } = DateTimeOffset.UtcNow;
    public string State { get; private set; } = "Running";

    private IntPtr _hPC = IntPtr.Zero;
    private SafeFileHandle? _ptyInWrite;  // we write to this to send input
    private SafeFileHandle? _ptyOutRead;  // we read from this to get output
    private ConPty.PROCESS_INFORMATION _pi;
    private CancellationTokenSource? _cts;

    // simple ring buffer
    private readonly byte[] _buffer = new byte[1024 * 1024];
    private int _bufLen = 0; // amount of valid data, capped to buffer size

    public Session(ILogger logger, string id, string name, string shell, string cwd, int cols, int rows)
    {
        _logger = logger;
        Id = id; Name = name; Shell = shell; Cwd = cwd; Cols = cols; Rows = rows;
    }

    public SessionSummary ToSummary() => new(
        Id, Name, State, Cols, Rows, Shell, Cwd, ShellPid, CreatedAt, LastActiveAt
    );

    private static readonly object _envLock = new();

    public void Start()
    {
        var sa = new ConPty.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<ConPty.SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!ConPty.CreatePipe(out var inRead, out var inWrite, ref sa, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!ConPty.CreatePipe(out var outRead, out var outWrite, ref sa, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var size = new ConPty.COORD((short)Cols, (short)Rows);
        int hr = ConPty.CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);
        if (hr != 0) throw new System.ComponentModel.Win32Exception(hr);

        inRead.Dispose(); // owned by ConPTY
        outWrite.Dispose(); // owned by ConPTY

        _ptyInWrite = inWrite;   // we write into console input
        _ptyOutRead = outRead;   // we read from console output

        var siEx = new ConPty.STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<ConPty.STARTUPINFOEX>();
        ConPty.SetPseudoConsoleAttribute(ref siEx, _hPC);

        string cmdLine = Quote(Shell);
        
        // Strategy: Modify current env vars under lock, spawn, then restore.
        // This avoids manual block construction issues.
        bool ok;
        lock (_envLock)
        {
            string? oldWmux = Environment.GetEnvironmentVariable("WMUX");
            string? oldSession = Environment.GetEnvironmentVariable("WMUX_SESSION");

            Environment.SetEnvironmentVariable("WMUX", "1");
            Environment.SetEnvironmentVariable("WMUX_SESSION", Name);

            try
            {
                 // 0 = Inherit Environment
                ok = ConPty.CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero, Cwd, ref siEx, out _pi);
            }
            finally
            {
                // Restore/Cleanup
                Environment.SetEnvironmentVariable("WMUX", oldWmux);
                Environment.SetEnvironmentVariable("WMUX_SESSION", oldSession);
            }
        }

        ConPty.FreeAttributeList(ref siEx);
        if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ShellPid = _pi.dwProcessId;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        _ = Task.Run(() => WaitExitAsync(_cts.Token));
    }

    public void Resize(int cols, int rows)
    {
        Cols = cols; Rows = rows;
        ConPty.ResizePseudoConsole(_hPC, new ConPty.COORD((short)cols, (short)rows));
    }

    public void WriteInput(byte[] data)
    {
        if (_ptyInWrite is null || _ptyInWrite.IsInvalid) return;
        lock (_writeLock)
        {
            using var s = new FileStream(_ptyInWrite, FileAccess.Write, 4096, false);
            s.Write(data, 0, data.Length);
            s.Flush();
        }
        LastActiveAt = DateTimeOffset.UtcNow;
    }

    public void AddListener(Action<byte[]> listener)
    {
        lock (_listeners) _listeners.Add(listener);
        // warm attach: send buffered tail
        if (_bufLen > 0)
        {
            var tail = new byte[_bufLen];
            Buffer.BlockCopy(_buffer, 0, tail, 0, _bufLen);
            listener(tail);
        }
    }

    public void RemoveListener(Action<byte[]> listener)
    {
        lock (_listeners) _listeners.Remove(listener);
    }

    public void Kill()
    {
        try { if (_pi.hProcess != IntPtr.Zero) Process.GetProcessById(_pi.dwProcessId).Kill(entireProcessTree: true); }
        catch { }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            if (_ptyOutRead is null) return;
            using var s = new FileStream(_ptyOutRead, FileAccess.Read, 4096, false);
            var buf = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                OnOutput(buf, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Session {id} ReadLoop failed", Id); }
    }

    private void OnOutput(byte[] data, int count)
    {
        // ring buffer append
        int copy = Math.Min(count, _buffer.Length);
        if (copy > 0)
        {
            // shift if needed to keep within buffer
            int available = _buffer.Length - _bufLen;
            if (available < copy)
            {
                int toDrop = copy - available;
                Buffer.BlockCopy(_buffer, toDrop, _buffer, 0, _bufLen - toDrop);
                _bufLen -= toDrop;
            }
            Buffer.BlockCopy(data, 0, _buffer, _bufLen, copy);
            _bufLen += copy;
        }
        LastActiveAt = DateTimeOffset.UtcNow;

        byte[] payload = new byte[count];
        Buffer.BlockCopy(data, 0, payload, 0, count);
        List<Action<byte[]>> snapshot;
        lock (_listeners) snapshot = _listeners.ToList();
        foreach (var l in snapshot) { try { l(payload); } catch { } }
    }

    private async Task WaitExitAsync(CancellationToken ct)
    {
        try
        {
            if (_pi.hProcess != IntPtr.Zero)
            {
                var proc = Process.GetProcessById(_pi.dwProcessId);
                await Task.Run(() => proc.WaitForExit(), ct).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            State = "Exited";
            try { if (_hPC != IntPtr.Zero) ConPty.ClosePseudoConsole(_hPC); } catch { }
            try { _ptyInWrite?.Dispose(); } catch { }
            try { _ptyOutRead?.Dispose(); } catch { }
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

