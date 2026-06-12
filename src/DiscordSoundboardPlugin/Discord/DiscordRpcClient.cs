namespace Loupedeck.DiscordSoundboardPlugin.Discord
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal enum IpcOpcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    internal sealed class DiscordRpcException : Exception
    {
        public DiscordRpcException(String message)
            : base(message)
        {
        }
    }

    // Speaks the Discord desktop client's local RPC protocol over the discord-ipc named pipe.
    // Wire format: 8-byte header (two little-endian int32s: opcode, payload length) + UTF-8 JSON.
    // Requests carry a nonce; the matching response carries the same nonce back.

    internal sealed class DiscordRpcClient : IDisposable
    {
        private readonly String _clientId;
        private readonly Object _writeLock = new Object();
        private readonly ConcurrentDictionary<String, TaskCompletionSource<JsonDocument>> _pending =
            new ConcurrentDictionary<String, TaskCompletionSource<JsonDocument>>();

        private NamedPipeClientStream _pipe;
        private Thread _readThread;
        private TaskCompletionSource<JsonDocument> _readyTcs;
        private volatile Boolean _disposed;
        private Int32 _closedRaised;

        public event EventHandler<String> Closed;

        // Unsolicited DISPATCH frames (subscribed events). Handlers must read the document
        // synchronously; it is disposed when the invocation returns.
        public event EventHandler<JsonDocument> DispatchReceived;

        public DiscordRpcClient(String clientId) => this._clientId = clientId;

        public Boolean IsConnected => !this._disposed && this._pipe?.IsConnected == true;

        public async Task<JsonDocument> ConnectAsync(CancellationToken ct)
        {
            for (var i = 0; i <= 9 && this._pipe == null; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(1000, ct).ConfigureAwait(false);
                    this._pipe = pipe;
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    throw;
                }
                catch
                {
                    pipe.Dispose();
                }
            }

            if (this._pipe == null)
            {
                throw new IOException("No discord-ipc pipe found. Is the Discord desktop app running?");
            }

            this._readyTcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);

            this._readThread = new Thread(this.ReadLoop) { IsBackground = true, Name = "DiscordIpcReadLoop" };
            this._readThread.Start();

            this.SendFrame(IpcOpcode.Handshake, JsonSerializer.Serialize(new Dictionary<String, Object>
            {
                ["v"] = 1,
                ["client_id"] = this._clientId,
            }));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using (timeout.Token.Register(() => this._readyTcs.TrySetException(new TimeoutException("Timed out waiting for Discord READY"))))
            {
                return await this._readyTcs.Task.ConfigureAwait(false);
            }
        }

        // `evt` is the top-level event field used by SUBSCRIBE/UNSUBSCRIBE commands.
        public async Task<JsonDocument> RequestAsync(String cmd, Object args, Int32 timeoutSeconds = 15, String evt = null)
        {
            if (!this.IsConnected)
            {
                throw new IOException("Not connected to Discord");
            }

            var nonce = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            this._pending[nonce] = tcs;
            try
            {
                var payload = new Dictionary<String, Object>
                {
                    ["cmd"] = cmd,
                    ["args"] = args ?? new Dictionary<String, Object>(),
                    ["nonce"] = nonce,
                };
                if (evt != null)
                {
                    payload["evt"] = evt;
                }
                this.SendFrame(IpcOpcode.Frame, JsonSerializer.Serialize(payload));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"Discord did not answer {cmd} within {timeoutSeconds}s"))))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                this._pending.TryRemove(nonce, out _);
            }
        }

        public void Dispose()
        {
            this._disposed = true;
            try
            {
                this._pipe?.Dispose();
            }
            catch
            {
            }
            this.FailPending("Connection disposed");
            this.RaiseClosed("Connection disposed");
        }

        private void SendFrame(IpcOpcode opcode, String json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var packet = new Byte[8 + payload.Length];
            BitConverter.GetBytes((Int32)opcode).CopyTo(packet, 0);
            BitConverter.GetBytes(payload.Length).CopyTo(packet, 4);
            payload.CopyTo(packet, 8);

            lock (this._writeLock)
            {
                this._pipe.Write(packet, 0, packet.Length);
                this._pipe.Flush();
            }
        }

        private void ReadLoop()
        {
            var reason = "Connection to Discord closed";
            try
            {
                while (!this._disposed && this._pipe.IsConnected)
                {
                    var header = this.ReadExact(8);
                    var opcode = (IpcOpcode)BitConverter.ToInt32(header, 0);
                    var length = BitConverter.ToInt32(header, 4);
                    var json = length > 0 ? Encoding.UTF8.GetString(this.ReadExact(length)) : "{}";

                    if (opcode == IpcOpcode.Ping)
                    {
                        this.SendFrame(IpcOpcode.Pong, json);
                    }
                    else if (opcode == IpcOpcode.Close)
                    {
                        reason = $"Discord closed the connection: {json}";
                        break;
                    }
                    else if (opcode == IpcOpcode.Frame)
                    {
                        this.DispatchFrame(json);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!this._disposed)
                {
                    reason = ex.Message;
                }
            }

            this.FailPending(reason);
            this.RaiseClosed(reason);
        }

        private void DispatchFrame(String json)
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("evt", out var evtProp) && evtProp.ValueKind == JsonValueKind.String ? evtProp.GetString() : null;
            var nonce = root.TryGetProperty("nonce", out var nonceProp) && nonceProp.ValueKind == JsonValueKind.String ? nonceProp.GetString() : null;

            if (nonce != null && this._pending.TryRemove(nonce, out var tcs))
            {
                if (evt == "ERROR")
                {
                    var message = "Discord RPC error";
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        var code = data.TryGetProperty("code", out var c) ? c.ToString() : "?";
                        var detail = data.TryGetProperty("message", out var m) ? m.GetString() : null;
                        message = $"Discord RPC error {code}: {detail}";
                    }
                    tcs.TrySetException(new DiscordRpcException(message));
                    doc.Dispose();
                }
                else if (!tcs.TrySetResult(doc))
                {
                    doc.Dispose();
                }
                return;
            }

            if (evt == "READY")
            {
                if (this._readyTcs?.TrySetResult(doc) != true)
                {
                    doc.Dispose();
                }
                return;
            }

            if (evt != null)
            {
                try
                {
                    this.DispatchReceived?.Invoke(this, doc);
                }
                catch
                {
                }
            }
            doc.Dispose();
        }

        private Byte[] ReadExact(Int32 count)
        {
            var buffer = new Byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = this._pipe.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Discord IPC pipe ended");
                }
                offset += read;
            }
            return buffer;
        }

        private void FailPending(String reason)
        {
            this._readyTcs?.TrySetException(new IOException(reason));
            foreach (var key in this._pending.Keys)
            {
                if (this._pending.TryRemove(key, out var tcs))
                {
                    tcs.TrySetException(new IOException(reason));
                }
            }
        }

        private void RaiseClosed(String reason)
        {
            if (Interlocked.Exchange(ref this._closedRaised, 1) == 0)
            {
                this.Closed?.Invoke(this, reason);
            }
        }
    }
}
