using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LibSample
{
    public class ThroughputTest : IExample
    {
        public const long TOTAL_BYTES = 1024L * 1024L * 16; // 256 MB
        public const int SEND_CHUNK_SIZE = 1024 * 1024; // 1 MB
        public const int SEND_CHUNK_COUNT = (int)(TOTAL_BYTES / SEND_CHUNK_SIZE);

        public class Server : INetEventListener, IDisposable
        {
            public int ReliableReceived { get; private set; }
            public bool HasCompleted { get; private set; }

            readonly NetManager _server;

            public TimeSpan TransferTime => _lastChunkReceiveTime - _peerConnectTime;

            DateTime _peerConnectTime;
            DateTime _lastChunkReceiveTime;

            public NetStatistics Stats => _server.Statistics;

            public Server(int latency, int jitter, int packetLoss)
            {
                _server = new NetManager(this)
                {
                    AutoRecycle = true,
                    UpdateTime = 1,
                    SimulatePacketLoss = true,
                    SimulationPacketLossChance = packetLoss,
                    SimulateLatency = true,
                    SimulationMinLatency = latency,
                    SimulationMaxLatency = latency + jitter,
                    EnableStatistics = true,
                    UnsyncedEvents = true
                };
                _server.Start(9050);
            }

            void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketErrorCode)
            {
                Console.WriteLine($"Server: error: {socketErrorCode}");
            }

            void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
                request.AcceptIfKey("ConnKey");
            }

            void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
            {
                if (++ReliableReceived == SEND_CHUNK_COUNT)
                {
                    _lastChunkReceiveTime = DateTime.UtcNow;
                    HasCompleted = true;
                }
            }

            void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
            }

            void INetEventListener.OnPeerConnected(NetPeer peer)
            {
                Console.WriteLine($"Server: client connected: {peer}");

                _peerConnectTime = DateTime.UtcNow;
            }

            void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
            {
                Console.WriteLine($"Server: client disconnected: {disconnectInfo.Reason}");
            }

            public void Dispose()
            {
                _server.Stop();
            }
        }

        public class Client : INetEventListener, IDisposable
        {
            public int ReliableSent;

            public bool HasCompleted { get; private set; }
            public bool IsRunning => _peer.ConnectionState == ConnectionState.Connected;

            readonly NetManager _client;
            NetPeer _peer;

            public NetStatistics Stats => _client.Statistics;

            public Client(int latency, int jitter, int packetLoss)
            {
                _client = new NetManager(this)
                {
                    UnsyncedEvents = true,
                    AutoRecycle = true,
                    SimulatePacketLoss = true,
                    SimulationPacketLossChance = packetLoss,
                    SimulateLatency = true,
                    SimulationMinLatency = latency,
                    SimulationMaxLatency = latency + jitter,
                    EnableStatistics = true
                };
                _client.Start();
            }

            public void SendReliable(byte[] data)
            {
                _peer.Send(data, DeliveryMethod.ReliableOrdered);
                ReliableSent++;
            }

            public void Connect()
            {
                _peer = _client.Connect("localhost", 9050, "ConnKey");
            }

            void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketErrorCode)
            {
                Console.WriteLine($"Client: error: {socketErrorCode}");
            }

            void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
                request.RejectForce();
            }

            void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
            {

            }

            void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
            }

            void INetEventListener.OnPeerConnected(NetPeer peer)
            {
                Task.Run(() =>
                {
                    var data = new byte[SEND_CHUNK_SIZE];
                    var r = new Random();

                    for (int i = 0; i < SEND_CHUNK_COUNT; i++)
                    {
                        r.NextBytes(data);
                        SendReliable(data);
                    }

                    HasCompleted = true;
                });
            }

            void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
            {
                Console.WriteLine($"Client: Disconnected {disconnectInfo.Reason}");
            }

            public void Dispose()
            {
                _client.Stop();
            }
        }

        public void Run()
        {
            Console.WriteLine("Testing Throughput...");

            int[] latencies = new int[] { 5, 10, 20, 40, 60, 80, 100, /*120, 140, 160, 180, 200, 250, 300*/ };
            int packetLoss = 1;

            var results = new List<TimeSpan>();

            foreach(var latency in latencies)
            {
                var jitter = Math.Max(1, (int)Math.Round(latency / 5f));

                var serverThread = new Thread(() => StartServer(latency, jitter, packetLoss, results));
                serverThread.Start();

                var  clientThread = new Thread(() => StartClient(latency, jitter, packetLoss));
                clientThread.Start();

                Console.WriteLine($"Processing, latency: {latency} ms, jitter: {jitter}, packet loss: {packetLoss} %...");

                serverThread.Join();
                clientThread.Join();
            }

            Console.WriteLine("Test has completed.");

            for (int i = 0; i < latencies.Length; i++)
            {
                var bytesPerSec = TOTAL_BYTES / results[i].TotalSeconds;
                Console.WriteLine($"{latencies[i]} ms -> {results[i]}\t{bytesPerSec / 1024:F2} kB/s");
            }
        }

        static void StartServer(int latency, int jitter, int packetLoss, List<TimeSpan> results)
        {
            using (Server s = new Server(latency, jitter, packetLoss))
            {
                while (!s.HasCompleted)
                    Thread.Sleep(100);

                Console.WriteLine("SERVER RECEIVED -> Reliable: " + s.ReliableReceived + " in " + s.TransferTime);
                Console.WriteLine("SERVER STATS:\n" + s.Stats);

                results.Add(s.TransferTime);
            }
        }

        static void StartClient(int latency, int jitter, int packetLoss)
        {
            using (Client c = new Client(latency, jitter, packetLoss))
            {
                c.Connect();

                while (!c.HasCompleted || c.IsRunning)
                    Thread.Sleep(100);

                Console.WriteLine("CLIENT SENT -> Reliable: " + c.ReliableSent);
                Console.WriteLine("CLIENT STATS:\n" + c.Stats);
            }
        }
    }
}
