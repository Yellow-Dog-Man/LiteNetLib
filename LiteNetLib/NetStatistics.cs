using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace LiteNetLib
{
    public sealed class NetStatistics
    {
        public bool TrackAveragePacketSize;
        public int PacketSizeAverageWindow = 256;

        public bool TrackAveragePacketMergeCount;
        public int PacketMergeAverageWindow = 256;

        private long _packetsSent;
        private long _packetsReceived;
        private long _bytesSent;
        private long _bytesReceived;
        private long _packetLoss;
        private long _windowWaits;

        ConcurrentQueue<long> _packetSizes = new ConcurrentQueue<long>();
        ConcurrentQueue<int> _packetMerges = new ConcurrentQueue<int>();

        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        public long PacketLoss => Interlocked.Read(ref _packetLoss);
        public long WindowWaitCount => Interlocked.Read(ref _windowWaits);

        public long PacketLossPercent
        {
            get
            {
                long sent = PacketsSent, loss = PacketLoss;

                return sent == 0 ? 0 : loss * 100 / sent;
            }
        }

        public double ComputeAveragePacketSize()
        {
            if (!TrackAveragePacketSize)
                throw new InvalidOperationException("Tracking average packet size is not enabled");

            return ComputeAverage(_packetSizes, size => (double)size);
        }

        public double ComputeAveragePacketMerge()
        {
            if (!TrackAveragePacketMergeCount)
                throw new InvalidOperationException("Tracking average packet merge is not enabled");

            return ComputeAverage(_packetMerges, count => (double)count);
        }

        double ComputeAverage<T>(ConcurrentQueue<T> data, Func<T, double> selector)
        {
            int count = 0;
            double sum = 0;

            foreach (var value in data)
            {
                count++;
                sum += selector(value);
            }

            if (count == 0)
                return 0;

            return sum / count;
        }

        void Store<T>(ConcurrentQueue<T> data, T value, int max)
        {
            data.Enqueue(value);

            while (data.Count > 0 && data.Count > max)
                data.TryDequeue(out _);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _packetsSent, 0);
            Interlocked.Exchange(ref _packetsReceived, 0);
            Interlocked.Exchange(ref _bytesSent, 0);
            Interlocked.Exchange(ref _bytesReceived, 0);
            Interlocked.Exchange(ref _packetLoss, 0);
            Interlocked.Exchange(ref _windowWaits, 0);

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            _packetSizes.Clear();
            _packetMerges.Clear();
#endif
        }

        public void IncrementPacketsSent(int merged)
        {
            Interlocked.Increment(ref _packetsSent);

            if (TrackAveragePacketMergeCount)
                Store(_packetMerges, merged, PacketMergeAverageWindow);
        }

        public void IncrementPacketsReceived()
        {
            Interlocked.Increment(ref _packetsReceived);
        }

        public void AddBytesSent(long bytesSent)
        {
            Interlocked.Add(ref _bytesSent, bytesSent);

            if (TrackAveragePacketSize)
                Store(_packetSizes, bytesSent, PacketSizeAverageWindow);
        }

        public void AddBytesReceived(long bytesReceived)
        {
            Interlocked.Add(ref _bytesReceived, bytesReceived);
        }

        public void IncrementPacketLoss()
        {
            Interlocked.Increment(ref _packetLoss);
        }

        public void AddPacketLoss(long packetLoss)
        {
            Interlocked.Add(ref _packetLoss, packetLoss);
        }

        public void IncrementWindowWaits()
        {
            Interlocked.Increment(ref _windowWaits);
        }

        public override string ToString()
        {
            return
                string.Format(
                    "BytesReceived: {0}\nPacketsReceived: {1}\nBytesSent: {2}\nPacketsSent: {3}\nPacketLoss: {4}\nPacketLossPercent: {5}\nWindow Wait Count: {6}",
                    BytesReceived,
                    PacketsReceived,
                    BytesSent,
                    PacketsSent,
                    PacketLoss,
                    PacketLossPercent,
                    WindowWaitCount);
        }
    }
}
