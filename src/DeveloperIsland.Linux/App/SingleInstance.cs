using System.Net.Sockets;
using System.Text;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland;

/// <summary>
/// One island per user session. The first instance listens on a Unix socket in the session's runtime
/// directory (private to the user); a second launch sends its request there ("show", "toggle",
/// "settings:Section") and exits. The global shortcut on GNOME uses the same path (<c>--toggle</c>).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>A request from a second launch, raised on a background thread.</summary>
    public event Action<string>? MessageReceived;

    public static string SocketPath(string name) => Path.Combine(AppPaths.Runtime, $"{name}.sock");

    /// <summary>Becomes the instance, or returns null when another one answers on the socket.</summary>
    public static SingleInstance? TryBecome(string name)
    {
        var path = SocketPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (TrySend(name, "ping"))
            {
                return null;
            }

            // A socket file left by a crashed instance: nobody listens, take it over.
            File.Delete(path);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(4);
        }
        catch (SocketException)
        {
            // Lost a race with another starting instance.
            socket.Dispose();
            return null;
        }

        var instance = new SingleInstance(socket, path);
        _ = instance.AcceptLoopAsync();
        return instance;
    }

    /// <summary>Sends a request to the running instance; false when none answers.</summary>
    public static bool TrySend(string name, string message)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 2000;
            socket.SendTimeout = 2000;
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(name)));
            socket.Send(Encoding.UTF8.GetBytes(message + "\n"));
            var buffer = new byte[16];
            return socket.Receive(buffer) > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                var buffer = new byte[512];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                await client.SendAsync("ok\n"u8.ToArray(), SocketFlags.None, _stop.Token);
                var message = Encoding.UTF8.GetString(buffer, 0, Math.Max(0, read)).Trim();
                if (message.Length > 0 && message != "ping")
                {
                    MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("startup", "Instance message failed", new { error = ex.GetType().Name });
            }
        }
    }
}
