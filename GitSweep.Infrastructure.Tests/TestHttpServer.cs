using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitSweep.Infrastructure.Tests;

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 for the security tests that need an
/// http origin (an authentication challenge, a <c>Set-Cookie</c> header).
/// Each connection gets one canned response chosen from the request head, and
/// is then closed. Plain sockets only: no extra package, no URL ACL.
/// </summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Task _acceptLoop;

    /// <param name="respond">Maps the request head (request line and
    /// headers) to the complete raw response.</param>
    public TestHttpServer(Func<string, string> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>Base URL, without a trailing slash.</summary>
    public string Url { get; }

    public IReadOnlyCollection<string> Requests => _requests;

    public static string Response(int status, string reason, string extraHeaders = "", string body = "") =>
        $"HTTP/1.1 {status} {reason}\r\n" +
        "Content-Type: text/plain\r\n" +
        $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n" +
        "Connection: close\r\n" +
        extraHeaders +
        "\r\n" +
        body;

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var request = head.ToString();
                _requests.Enqueue(request);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(request)), _stop.Token);
                await stream.FlushAsync(_stop.Token);
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }
        catch (Exception)
        {
            // The client went away; nothing to report.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Stopping the listener ends the loop.
        }

        _stop.Dispose();
    }
}
