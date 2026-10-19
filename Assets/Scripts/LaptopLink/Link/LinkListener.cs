using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The TCP listener of the link port. It listens only while at least one lease from <see cref="Acquire"/> is held and
    /// closes its socket when the last lease is released: it then stops accepting, while exchanges already running finish
    /// on their own. A bind failure (port busy) never throws: it is logged and shown in <see cref="Status"/>. Plain C#;
    /// the accept loop runs on a worker, each client is handed to the handler on its own task.
    /// </summary>
    public sealed class LinkListener : IDisposable
    {
        public const int LinkPort = 47810;

        readonly object gate = new object();
        readonly Func<TcpClient, CancellationToken, Task> handle;
        readonly IPAddress address;
        readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        TcpListener listener;
        int leases;
        bool disposed;
        string status = "Not listening";

        public int Port { get; }
        /// <summary>Raised on the thread that acquired or released a lease.</summary>
        public event Action StatusChanged;

        public LinkListener(Func<TcpClient, CancellationToken, Task> handler, int port = LinkPort, IPAddress bindAddress = null)
        {
            handle = handler;
            Port = port;
            address = bindAddress ?? IPAddress.Any;
        }

        public bool IsListening { get { lock (gate) return listener != null; } }
        public int LeaseCount { get { lock (gate) return leases; } }
        public string Status { get { lock (gate) return status; } }
        /// <summary>Leases are held but the socket could not be opened.</summary>
        public bool BindFailed { get { lock (gate) return leases > 0 && listener == null; } }

        /// <summary>Starts listening if this is the first lease. Dispose the lease to give it back.</summary>
        public IDisposable Acquire()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(LinkListener));
                leases++;
                if (listener == null) Start();
            }
            StatusChanged?.Invoke();
            return new Lease(this);
        }

        void Release()
        {
            lock (gate)
            {
                if (leases > 0 && --leases == 0) Stop();
            }
            StatusChanged?.Invoke();
        }

        void Start()
        {
            try
            {
                var created = new TcpListener(address, Port);
                created.Start();
                listener = created;
                status = "Listening on port " + Port;
                Task.Run(() => AcceptLoop(created));
            }
            catch (Exception e)
            {
                listener = null;
                status = "Cannot listen on port " + Port + ": " + e.Message;
                Debug.LogWarning("[Link] " + status);
            }
        }

        void Stop()
        {
            var closing = listener;
            listener = null;
            status = "Not listening";
            try { closing?.Stop(); }
            catch (Exception) { }
        }

        bool IsCurrent(TcpListener l)
        {
            lock (gate) return listener == l;
        }

        async Task AcceptLoop(TcpListener l)
        {
            while (IsCurrent(l))
            {
                TcpClient client;
                try
                {
                    client = await l.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (!IsCurrent(l)) return;
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }
                _ = Serve(client);
            }
        }

        async Task Serve(TcpClient client)
        {
            try
            {
                await handle(client, shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Link] connection failed: " + e.GetType().Name + " " + e.Message);
            }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                leases = 0;
                Stop();
            }
            shutdown.Cancel();
            StatusChanged?.Invoke();
        }

        sealed class Lease : IDisposable
        {
            LinkListener owner;

            public Lease(LinkListener listener) { owner = listener; }

            public void Dispose()
            {
                var o = Interlocked.Exchange(ref owner, null);
                if (o != null) o.Release();
            }
        }
    }
}
