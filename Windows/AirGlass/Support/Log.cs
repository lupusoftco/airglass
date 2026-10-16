using System;
using System.Diagnostics;
using System.IO;

namespace AirGlass.Support;

/// <summary>
/// Diagnostics, written to %LOCALAPPDATA%\AirGlass\AirGlass.log (replaced on
/// every launch) and to the debugger output. Tokens are never logged.
/// </summary>
public static class Log
{
    // Initialized in this order: the writer needs the path.
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirGlass", "AirGlass.log");

    private static readonly object Gate = new();
    private static readonly StreamWriter? Writer = Open();

    public static void Server(string message) => Write("server", message);
    public static void Signaling(string message) => Write("signaling", message);
    public static void WebRtc(string message) => Write("webrtc", message);
    public static void Capture(string message) => Write("capture", message);
    public static void App(string message) => Write("app", message);

    private static void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
        Debug.WriteLine(line);
        if (Writer == null) return;
        lock (Gate)
        {
            try
            {
                Writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Disk full or the file vanished; logging is best effort.
            }
        }
    }

    private static StreamWriter? Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
