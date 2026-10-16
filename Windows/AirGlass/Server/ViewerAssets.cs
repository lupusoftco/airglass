using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace AirGlass.Server;

/// <summary>
/// The phone-side viewer page: the same files as the macOS app (the repo's
/// Viewer folder, embedded at build time), kept in memory. Only these exact
/// paths are served.
/// </summary>
public sealed class ViewerAssets
{
    public sealed record Asset(byte[] Data, string ContentType);

    private readonly Dictionary<string, Asset> _assets = new();

    public ViewerAssets()
    {
        var files = new (string Path, string Name, string Type)[]
        {
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/viewer.js", "viewer.js", "text/javascript; charset=utf-8"),
            ("/viewer.css", "viewer.css", "text/css; charset=utf-8"),
            // Silent clip that keeps the phone awake (see viewer.js).
            ("/keepawake.mp4", "keepawake.mp4", "video/mp4"),
        };
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var (path, name, type) in files)
        {
            using var stream = assembly.GetManifestResourceStream("Viewer/" + name);
            if (stream == null) continue;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var data = memory.ToArray();
            if (type.StartsWith("text/")) data = Localize(data);
            _assets[path] = new Asset(data, type);
        }
    }

    public Asset? this[string path] => _assets.TryGetValue(path, out var asset) ? asset : null;

    /// <summary>The page tells the viewer to rescan "the QR code on the Mac";
    /// here it is on the PC.</summary>
    private static byte[] Localize(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).Replace("Mac'teki", "bilgisayardaki");
        return Encoding.UTF8.GetBytes(text);
    }
}
