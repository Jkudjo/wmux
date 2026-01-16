# WinMux

A native Terminal Multiplexer for Windows, written in .NET 8.

## Features

- **Native Performance**: Uses Windows PTY (ConPTY) for true terminal emulation.
- **Background Daemon**: Sessions persist even if the client closes.
- **Resilience**: Auto-starts the daemon if it's not running.
- **Production Ready**: Supports graceful detach, auto-window resizing, and raw mode.

## Installation

1.  Navigate to the `dist` folder:
    ```powershell
    cd dist
    ```
2.  Add this folder to your **PATH**.
3.  Done!

## Usage

### Create a Session

```powershell
wmux new -n myproject
```

### List Sessions

```powershell
wmux ls
```

### Attach to a Session

```powershell
wmux attach myproject
```

### Detach (Keep running)

Press `Ctrl+b` then `d`.

### Kill a Session

```powershell
wmux kill myproject
```
