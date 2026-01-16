using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using WinMux.Core.Protocol;

static void PrintUsage()
{
    Console.WriteLine("wmux commands:");
    Console.WriteLine("  wmux ping                 - test daemon connectivity");
    Console.WriteLine("  wmux ls                   - list sessions");
    Console.WriteLine("  wmux new [-n name] [-s shell] [-C cwd] [-c cols] [-r rows]");
    Console.WriteLine("  wmux attach <id|name>     - attach and interact");
    Console.WriteLine("  wmux kill <id|name>       - kill a session");
    Console.WriteLine("  wmux resize <id|name> <cols> <rows>");
    Console.WriteLine("  wmux help                 - show usage");
}

if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
{
    PrintUsage();
    return;
}

var command = args[0];
var pipeName = "winmuxd"; // default

switch (command)
{
    case "ping":
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new PingRequest());
            var ev = await Serialization.ReadFramedAsync<EventMessage>(pipe);
            if (ev is PongEvent pong)
            {
                Console.WriteLine($"pong: {pong.ServerTime:O}");
            }
            else if (ev is ErrorEvent err)
            {
                Console.WriteLine($"error: {err.Code} {err.Message}");
            }
            else
            {
                if (ev is null) Console.WriteLine("null response (disconnected)");
                else Console.WriteLine($"unexpected response: {ev.GetType().Name}");
            }
        });
        break;

    case "ls":
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new ListRequest());
            var ev = await Serialization.ReadFramedAsync<EventMessage>(pipe);
            if (ev is SessionsEvent s)
            {
                if (s.Sessions.Count == 0)
                {
                    Console.WriteLine("No sessions.");
                }
                else
                {
                    foreach (var sess in s.Sessions)
                    {
                        Console.WriteLine($"{sess.Id}\t{sess.Name}\t{sess.State} {sess.Cols}x{sess.Rows} {sess.Shell}");
                    }
                }
            }
            else if (ev is ErrorEvent err)
            {
                Console.WriteLine($"error: {err.Code} {err.Message}");
            }
            else
            {
                if (ev is null) Console.WriteLine("null response (disconnected)");
                else Console.WriteLine($"unexpected response: {ev.GetType().Name}");
            }
        });
        break;

    case "new":
    {
        string name = GetArgValue(args, "-n") ?? "";
        string shell = GetArgValue(args, "-s") ?? "";
        string cwd = GetArgValue(args, "-C") ?? "";
        int? cols = int.TryParse(GetArgValue(args, "-c"), out var c) ? c : null;
        int? rows = int.TryParse(GetArgValue(args, "-r"), out var r) ? r : null;
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new CreateSessionRequest(name, string.IsNullOrWhiteSpace(shell)? null : shell, string.IsNullOrWhiteSpace(cwd)? null : cwd, null, cols, rows));
            var ev = await Serialization.ReadFramedAsync<EventMessage>(pipe);
            if (ev is CreatedEvent created)
            {
                Console.WriteLine(created.SessionId);
            }
            else if (ev is ErrorEvent err)
            {
                Console.WriteLine($"error: {err.Code} {err.Message}");
            }
        });
        break;
    }

    case "attach" when args.Length >= 2:
    {
        var id = args[1];
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new AttachRequest(id));
            var ev = await Serialization.ReadFramedAsync<EventMessage>(pipe);
            if (ev is ErrorEvent err)
            {
                Console.WriteLine($"error: {err.Code} {err.Message}");
                return;
            }
            if (ev is not AttachedEvent att)
            {
                Console.WriteLine("unexpected response");
                return;
            }

            // Enter Raw Mode
            using var _ = new ConsoleMode();

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var quitCts = new CancellationTokenSource();
            string sessionName = att.SessionId.Substring(0, 8); // simplified name

            // Helper to draw status bar
            async Task DrawStatusBar(int cols, int rows)
            {
                // Save cursor, Move to bottom, Inverse, Write, Clear Line, Reset, Restore cursor
                // \x1b7 = Save Cursor (DEC)
                // \x1b[{rows};1H = Move to last line
                // \x1b[7m = Inverse
                // \x1b[K = Clear to end
                // \x1b[m = Reset attributes
                // \x1b8 = Restore Cursor (DEC)
                string msg = $" WinMux | {sessionName} | Ctrl+b, d to Detach";
                string seq = $"\x1b7\x1b[{rows};1H\x1b[7m{msg}\x1b[K\x1b[m\x1b8";
                await stdout.WriteAsync(System.Text.Encoding.UTF8.GetBytes(seq), quitCts.Token);
                await stdout.FlushAsync(quitCts.Token);
            }

            // Helper to set scroll region (exclude bottom line)
            async Task SetScrollRegion(int rows)
            {
                // \x1b[1;{rows-1}r = Set scrolling region top to bottom-1
                string seq = $"\x1b[1;{rows - 1}r";
                await stdout.WriteAsync(System.Text.Encoding.UTF8.GetBytes(seq), quitCts.Token);
                await stdout.FlushAsync(quitCts.Token);
            }

            // Initial Setup
            int initialRows = Console.WindowHeight;
            int initialCols = Console.WindowWidth;
            await SetScrollRegion(initialRows);
            await DrawStatusBar(initialCols, initialRows);
            // Send resize immediately for the "inner" size
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new ResizeRequest(att.SessionId, initialCols, initialRows - 1), quitCts.Token);

            var readEvents = Task.Run(async () =>
            {
                try
                {
                    while (!quitCts.Token.IsCancellationRequested)
                    {
                        var evt = await Serialization.ReadFramedAsync<EventMessage>(pipe, quitCts.Token);
                        if (evt is OutputEvent o)
                        {
                            await stdout.WriteAsync(o.Data, quitCts.Token);
                            await stdout.FlushAsync(quitCts.Token);
                        }
                        else if (evt is ExitEvent e)
                        {
                            // session exited
                            break;
                        }
                        else if (evt is ErrorEvent ee)
                        {
                             break;
                        }
                    }
                }
                catch { }
                finally { quitCts.Cancel(); }
            });

            var readInput = Task.Run(async () =>
            {
                var buf = new byte[1024];
                bool prefix = false;

                try
                {
                    while (!quitCts.Token.IsCancellationRequested)
                    {
                        int n = await stdin.ReadAsync(buf, 0, buf.Length, quitCts.Token);
                        if (n <= 0) break;

                        var sendBuf = new List<byte>(n);
                        for (int i = 0; i < n; i++)
                        {
                            byte b = buf[i];
                            if (prefix)
                            {
                                if (b == (byte)'d') 
                                {
                                    quitCts.Cancel();
                                    return;
                                }
                                else if (b == 0x02) { sendBuf.Add(b); }
                                else { sendBuf.Add(0x02); sendBuf.Add(b); }
                                prefix = false;
                            }
                            else
                            {
                                if (b == 0x02) { prefix = true; }
                                else { sendBuf.Add(b); }
                            }
                        }

                        if (sendBuf.Count > 0)
                        {
                            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new InputRequest(att.SessionId, sendBuf.ToArray()), quitCts.Token);
                        }
                    }
                }
                catch { }
                finally { quitCts.Cancel(); }
            });

            var resizeLoop = Task.Run(async () =>
            {
                int lastCols = initialCols;
                int lastRows = initialRows;
                try
                {
                    while (!quitCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(500, quitCts.Token);
                        int c = Console.WindowWidth;
                        int r = Console.WindowHeight;
                        if (c != lastCols || r != lastRows)
                        {
                            lastCols = c; lastRows = r;
                            // Reset Scroll Region for new size
                            await SetScrollRegion(r);
                            await DrawStatusBar(c, r);
                            // Tell Daemon size is (Height - 1)
                            try  { await Serialization.WriteFramedAsync<RequestMessage>(pipe, new ResizeRequest(att.SessionId, c, r - 1), quitCts.Token); } catch { } 
                        }
                    }
                }
                catch { }
            });

            await Task.WhenAny(readEvents, readInput);
            
            // Restore Scroll Region on exit
            // \x1b[r = Reset scroll region
            await stdout.WriteAsync(System.Text.Encoding.UTF8.GetBytes("\x1b[r"));
            
        });
        break;
    }

    case "kill" when args.Length >= 2:
    {
        var id = args[1];
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new KillRequest(id));
        });
        break;
    }

    case "resize" when args.Length >= 4:
    {
        var id = args[1];
        if (!int.TryParse(args[2], out var cols) || !int.TryParse(args[3], out var rows))
        {
            Console.WriteLine("resize: invalid cols/rows");
            break;
        }
        await WithPipeAsync(pipeName, async pipe =>
        {
            await Serialization.WriteFramedAsync<RequestMessage>(pipe, new ResizeRequest(id, cols, rows));
        });
        break;
    }

    default:
        Console.WriteLine($"Unknown command: {command}");
        PrintUsage();
        break;
}

static async Task WithPipeAsync(string pipeName, Func<NamedPipeClientStream, Task> action)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await client.ConnectAsync(500);
    }
    catch (TimeoutException)
    {
        // Try to spawn daemon
        Console.WriteLine("Daemon not running. Starting...");
        string daemonExe = "WinMux.Daemon.exe";
        // Attempt to find it relative to us
        if (!File.Exists(daemonExe))
        {
             // Try standard net8.0 output path if running from source/build
             string debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "WinMux.Daemon", "bin", "Debug", "net8.0", "WinMux.Daemon.exe");
             if (File.Exists(debugPath)) daemonExe = Path.GetFullPath(debugPath);
             else
             {
                 // Try side-by-side
                 string sideBySide = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinMux.Daemon.exe");
                 if (File.Exists(sideBySide)) daemonExe = sideBySide;
             }
        }

        if (File.Exists(daemonExe))
        {
            try 
            {
                Process.Start(new ProcessStartInfo(daemonExe) { 
                    UseShellExecute = true, 
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                // Wait for it to listen
                int retries = 10;
                while (retries-- > 0)
                {
                    await Task.Delay(200);
                    try { await client.ConnectAsync(100); break; } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start daemon: {ex.Message}");
                return;
            }
        }
        else
        {
             Console.WriteLine("Daemon executable not found. Please start WinMux.Daemon manually.");
             return;
        }
    }
    
    if (!client.IsConnected)
    {
         // Final attempt if we fell through
         try { await client.ConnectAsync(100); } catch {}
    }

    if (!client.IsConnected)
    {
        Console.WriteLine("Failed to connect to daemon.");
        return;
    }

    await action(client);
}

static string? GetArgValue(string[] args, string flag)
{
    int idx = Array.IndexOf(args, flag);
    if (idx >= 0 && idx < args.Length - 1) return args[idx + 1];
    return null;
}

// Low-level Console P/Invoke
internal class ConsoleMode : IDisposable
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;

    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;

    private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    private const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly IntPtr _hInput;
    private readonly IntPtr _hOutput;
    private readonly uint _originalInMode;
    private readonly uint _originalOutMode;

    public ConsoleMode()
    {
        _hInput = GetStdHandle(STD_INPUT_HANDLE);
        _hOutput = GetStdHandle(STD_OUTPUT_HANDLE);

        if (!GetConsoleMode(_hInput, out _originalInMode))
             throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!GetConsoleMode(_hOutput, out _originalOutMode))
             throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        // Raw Input: Disable line input, echo, and processed input (Ctrl+C handling handled by shell)
        uint newInMode = _originalInMode & ~(ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT | ENABLE_PROCESSED_INPUT);
        // Ensure VIRTUAL_TERMINAL_INPUT is set if you want it (usually enabled by default in modern Windows Terminal)
        // newInMode |= 0x0200; 

        // Raw Output: Ensure VT processing is ON
        uint newOutMode = _originalOutMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING;

        SetConsoleMode(_hInput, newInMode);
        SetConsoleMode(_hOutput, newOutMode);
    }

    public void Dispose()
    {
        SetConsoleMode(_hInput, _originalInMode);
        SetConsoleMode(_hOutput, _originalOutMode);
    }
}
