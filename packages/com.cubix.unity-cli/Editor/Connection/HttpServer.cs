using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Cubix.UnityCli
{
    internal sealed class CommandActivitySnapshot
    {
        public bool busy;
        public string command;
        public string requestId;
        public string startedAtUtc;
        public long durationMs;
        public int queuedCount;
    }

    internal static class HttpServer
    {
        private const string PortEditorPrefKey = "Cubix.UnityCli.Port";

        private sealed class QueuedRequest
        {
            public CommandRequest Request { get; set; }
            public TaskCompletionSource<object> Completion { get; set; }
        }

        private static readonly ConcurrentQueue<QueuedRequest> Queue = new ConcurrentQueue<QueuedRequest>();
        private static readonly object ActivityLock = new object();

        private static HttpListener _listener;
        private static bool _processing;
        private static int _queuedCount;
        private static string _activeCommand;
        private static string _activeRequestId;
        private static DateTime _processingStartedAtUtc;
        private static CancellationTokenSource _keepAliveCancellation;
        private static Task _keepAliveTask;

        static HttpServer()
        {
            EditorApplication.update += ProcessQueue;
            EditorApplication.quitting += Stop;
        }

        public static int Port { get; private set; }

        public static int AdvertisedPort
        {
            get
            {
                if (Port > 0)
                {
                    return Port;
                }

                var preferredPort = EditorPrefs.GetInt(PortEditorPrefKey, 0);
                return IsSupportedPort(preferredPort) ? preferredPort : 0;
            }
        }

        public static bool IsRunning => _listener != null && _listener.IsListening;

        public static string Url => Port > 0 ? "http://127.0.0.1:" + Port : null;

        public static string AdvertisedUrl => BuildLoopbackUrl(AdvertisedPort);

        public static string LastError { get; private set; }

        public static bool Start()
        {
            if (IsRunning)
            {
                return true;
            }

            var port = FindAvailablePort(EditorPrefs.GetInt(PortEditorPrefKey, 0));
            if (port <= 0)
            {
                LastError = "Could not find an available loopback port.";
                return false;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
                Port = port;
                EditorPrefs.SetInt(PortEditorPrefKey, port);
                LastError = null;
                StartKeepAliveLoop();
                Task.Run(ListenLoop);
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Stop();
                return false;
            }
        }

        private static async Task ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context));
            }
        }

        private static async Task HandleContextAsync(HttpListenerContext context)
        {
            object payload;
            var statusCode = 200;

            try
            {
                if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/health")
                {
                    payload = new CommandSuccessResponse("OK", new
                    {
                        projectName = ConnectorPaths.ProjectName,
                        port = Port
                    });
                }
                else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/status")
                {
                    if (HeartbeatService.TryGetCachedStatusSnapshot(out var snapshot))
                    {
                        payload = new CommandSuccessResponse("Status", snapshot);
                    }
                    else
                    {
                        statusCode = 503;
                        payload = new CommandErrorResponse("Status snapshot is not available yet.");
                    }
                }
                else if (context.Request.HttpMethod == "POST" && context.Request.Url.AbsolutePath == "/command")
                {
                    payload = await DispatchCommandAsync(context.Request);
                }
                else
                {
                    statusCode = 404;
                    payload = new CommandErrorResponse("Endpoint not found.");
                }
            }
            catch (Exception exception)
            {
                statusCode = 500;
                payload = new CommandErrorResponse("Unhandled server error.", errors: new[]
                {
                    new
                    {
                        type = exception.GetType().FullName,
                        message = exception.Message
                    }
                });
            }

            var body = JsonConvert.SerializeObject(payload, Formatting.Indented);
            var buffer = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.StatusCode = statusCode;
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }

        private static async Task<object> DispatchCommandAsync(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                var json = await reader.ReadToEndAsync();
                var payload = JObject.Parse(json);
                var commandRequest = new CommandRequest
                {
                    command = payload.Value<string>("command"),
                    @params = payload["params"] as JObject,
                    requestId = payload.Value<string>("requestId")
                };

                var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (ActivityLock)
                {
                    if (_processing || _queuedCount > 0)
                    {
                        return BuildBusyResponse(commandRequest);
                    }

                    Queue.Enqueue(new QueuedRequest
                    {
                        Request = commandRequest,
                        Completion = completion
                    });
                    _queuedCount++;
                }

                return await completion.Task;
            }
        }

        private static void ProcessQueue()
        {
            QueuedRequest next;
            lock (ActivityLock)
            {
                if (_processing || !Queue.TryDequeue(out next))
                {
                    return;
                }

                _processing = true;
                _queuedCount = Math.Max(0, _queuedCount - 1);
                _activeCommand = next.Request?.command;
                _activeRequestId = next.Request?.requestId;
                _processingStartedAtUtc = DateTime.UtcNow;
            }

            try
            {
                next.Completion.TrySetResult(CommandRouter.Route(next.Request));
            }
            catch (Exception exception)
            {
                next.Completion.TrySetResult(new CommandErrorResponse("Command dispatch failed.", errors: new[]
                {
                    new
                    {
                        type = exception.GetType().FullName,
                        message = exception.Message
                    }
                }));
            }
            finally
            {
                lock (ActivityLock)
                {
                    _processing = false;
                    _activeCommand = null;
                    _activeRequestId = null;
                    _processingStartedAtUtc = default;
                }
            }
        }

        public static CommandActivitySnapshot GetCommandActivitySnapshot()
        {
            lock (ActivityLock)
            {
                return new CommandActivitySnapshot
                {
                    busy = _processing,
                    command = _activeCommand,
                    requestId = _activeRequestId,
                    startedAtUtc = _processing ? _processingStartedAtUtc.ToString("o") : null,
                    durationMs = _processing
                        ? (long)Math.Max((DateTime.UtcNow - _processingStartedAtUtc).TotalMilliseconds, 0d)
                        : 0L,
                    queuedCount = _queuedCount
                };
            }
        }

        public static void Stop()
        {
            StopKeepAliveLoop();
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
            }
            finally
            {
                _listener = null;
                Port = 0;
                lock (ActivityLock)
                {
                    _processing = false;
                    _queuedCount = 0;
                    _activeCommand = null;
                    _activeRequestId = null;
                    _processingStartedAtUtc = default;
                    while (Queue.TryDequeue(out var pending))
                    {
                        pending?.Completion?.TrySetResult(new CommandErrorResponse("Command was canceled because the Cubix Unity CLI connection stopped."));
                    }
                }
            }
        }

        private static object BuildBusyResponse(CommandRequest request)
        {
            var snapshot = GetCommandActivitySnapshot();
            return new CommandErrorResponse("Another Cubix command is already running.", errors: new[]
            {
                new
                {
                    code = "command_busy",
                    requestedCommand = request?.command,
                    activeCommand = snapshot.command,
                    activeRequestId = snapshot.requestId,
                    activeStartedAtUtc = snapshot.startedAtUtc,
                    activeDurationMs = snapshot.durationMs,
                    queuedCount = snapshot.queuedCount
                }
            });
        }

        private static void StartKeepAliveLoop()
        {
            StopKeepAliveLoop();
            _keepAliveCancellation = new CancellationTokenSource();
            _keepAliveTask = Task.Run(() => CommandKeepAliveLoopAsync(_keepAliveCancellation.Token));
        }

        private static void StopKeepAliveLoop()
        {
            try
            {
                _keepAliveCancellation?.Cancel();
            }
            catch
            {
            }
            finally
            {
                _keepAliveCancellation?.Dispose();
                _keepAliveCancellation = null;
                _keepAliveTask = null;
            }
        }

        private static async Task CommandKeepAliveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = GetCommandActivitySnapshot();
                    if (IsRunning && (snapshot.busy || snapshot.queuedCount > 0))
                    {
                        HeartbeatService.RefreshBusyKeepAlive(snapshot);
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
        }

        private static int FindAvailablePort(int preferredPort)
        {
            if (IsSupportedPort(preferredPort) && CanBind(preferredPort))
            {
                return preferredPort;
            }

            for (var port = 48061; port <= 48120; port++)
            {
                if (port != preferredPort && CanBind(port))
                {
                    return port;
                }
            }

            return 0;
        }

        private static bool IsSupportedPort(int port)
        {
            return port >= 48061 && port <= 48120;
        }

        private static string BuildLoopbackUrl(int port)
        {
            return IsSupportedPort(port) ? "http://127.0.0.1:" + port : null;
        }

        private static bool CanBind(int port)
        {
            try
            {
                var probe = new HttpListener();
                probe.Prefixes.Add("http://127.0.0.1:" + port + "/");
                probe.Start();
                probe.Stop();
                probe.Close();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
