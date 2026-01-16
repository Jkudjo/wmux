namespace WinMux.Daemon.Configuration;

public class WinMuxConfig
{
    public string DefaultShell { get; set; } = "pwsh.exe";
    public string DefaultCwd { get; set; } = "%USERPROFILE%";
    public int MaxSessions { get; set; } = 50;
    public int BufferSize { get; set; } = 4096;
}
