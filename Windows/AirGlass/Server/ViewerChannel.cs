using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using AirGlass.Support;

namespace AirGlass.Server;

/// <summary>
/// An authenticated viewer's signaling channel over plain HTTP: the phone
/// POSTs its messages and long-polls GET for ours. Messages are JSON objects
/// with a "type" field.
///
/// <see cref="Send"/> and <see cref="Close"/> may be called from any thread;
/// the events are raised on the UI thread.
/// </summary>
public sealed class ViewerChannel
{
    /// <summary>How long a poll is held open when there is nothing to deliver.</summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Without a pending poll for this long, the viewer is considered
    /// gone (tab closed, phone locked, network lost). Long enough to ride out a
    /// brief Wi-Fi hiccup or app switch.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly SynchronizationContext? _ui;
    /// <summary>Serialized JSON objects waiting for the next poll.</summary>
    private readonly List<string> _outbox = new();
    private readonly Timer _watchdog;
    private HttpConnection? _pendingPoll;
    private Timer? _pollTimer;
    private DateTime _lastSeen = DateTime.UtcNow;
    private bool _isClosed;

    public ViewerChannel(string deviceName, string remoteAddress, SynchronizationContext? ui)
    {
        DeviceName = deviceName;
        RemoteAddress = remoteAddress;
        _ui = ui;
        _watchdog = new Timer(_ => CheckLiveness(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>"iPhone — Safari"</summary>
    public string DeviceName { get; }
    /// <summary>The viewer's IP address.</summary>
    public string RemoteAddress { get; }

    /// <summary>Raised on the UI thread, in arrival order.</summary>
    public event Action<JsonObject>? MessageReceived;
    /// <summary>Raised once on the UI thread when the channel ends for any reason.</summary>
    public event Action? Closed;

    /// <summary>Server-internal; called once, on any thread, when the channel ends.</summary>
    internal Action? Ended;

    // MARK: - Public

    public void Send(JsonObject message)
    {
        var json = message.ToJsonString();
        Log.Signaling($"→ {(message["type"] as JsonValue)?.ToString() ?? "?"}");
        lock (_gate)
        {
            if (_isClosed) return;
            _outbox.Add(json);
            Flush();
        }
    }

    public void Close() => End("closed by the computer");

    // MARK: - Server side

    /// <summary>A message the viewer POSTed.</summary>
    internal void Receive(JsonObject message)
    {
        lock (_gate)
        {
            if (_isClosed) return;
            _lastSeen = DateTime.UtcNow;
        }
        Log.Signaling($"← {(message["type"] as JsonValue)?.ToString() ?? "?"}");
        Post(() => MessageReceived?.Invoke(message));
    }

    /// <summary>A long-poll GET: answered now if messages are waiting,
    /// otherwise when one is sent or after the poll timeout with an empty list.</summary>
    internal void Poll(HttpConnection http)
    {
        lock (_gate)
        {
            if (_isClosed)
            {
                http.Respond(410, "Gone");
                return;
            }
            _lastSeen = DateTime.UtcNow;

            // Only one poll is held at a time; release an older one.
            if (_pendingPoll != null)
            {
                var previous = _pendingPoll;
                _pendingPoll = null;
                previous.RespondJsonArray(Array.Empty<string>());
            }

            _pendingPoll = http;
            Flush();
            if (_pendingPoll != http) return;

            _pollTimer?.Dispose();
            _pollTimer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_pendingPoll != http) return;
                    _pendingPoll = null;
                    _lastSeen = DateTime.UtcNow;
                }
                http.RespondJsonArray(Array.Empty<string>());
            }, null, PollTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The viewer said goodbye (page closed).</summary>
    internal void CloseByViewer() => End("closed by the viewer");

    // MARK: - Private

    /// <summary>Must be called with the lock held.</summary>
    private void Flush()
    {
        if (_outbox.Count == 0 || _pendingPoll == null) return;
        var poll = _pendingPoll;
        _pendingPoll = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _lastSeen = DateTime.UtcNow;
        if (poll.IsFinished) return;
        var messages = _outbox.ToArray();
        _outbox.Clear();
        poll.RespondJsonArray(messages);
    }

    private void CheckLiveness()
    {
        lock (_gate)
        {
            if (_isClosed) return;
            if (_pendingPoll is { IsFinished: false })
            {
                _lastSeen = DateTime.UtcNow;
                return;
            }
            if (DateTime.UtcNow - _lastSeen <= IdleTimeout) return;
        }
        End("viewer stopped polling");
    }

    private void End(string reason)
    {
        lock (_gate)
        {
            if (_isClosed) return;
            _isClosed = true;
            _watchdog.Dispose();
            _pollTimer?.Dispose();
            _pollTimer = null;
            _pendingPoll?.Respond(410, "Gone");
            _pendingPoll = null;
            _outbox.Clear();
        }
        Log.Signaling($"Channel ended: {reason}");
        var ended = Interlocked.Exchange(ref Ended, null);
        ended?.Invoke();
        Post(() =>
        {
            var closed = Closed;
            Closed = null;
            closed?.Invoke();
        });
    }

    private void Post(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else action();
    }
}
