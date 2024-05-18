using System;
using System.Runtime.InteropServices;

namespace LiteNetLib
{
    internal sealed class ReliableChannel : BaseChannel
    {
        private struct PendingPacket
        {
            private NetPacket _packet;
            private long _timeStamp;
            private bool _isSent;

            public bool IsSent => _isSent;

            public override string ToString()
            {
                return _packet == null ? "Empty" : _packet.Sequence.ToString();
            }

            public void Init(NetPacket packet)
            {
                _packet = packet;
                _isSent = false;
            }

            //Returns true if there is a pending packet inside
            public bool TrySend(long currentTime, NetPeer peer, ref int packetsInFlight)
            {
                if (_packet == null)
                    return false;

                if (_isSent) //check send time
                {
                    if (!NeedsResend(currentTime, peer, true))
                        return true;
                }
                else
                    packetsInFlight++; // Only increment this when it's actually been sent

                _timeStamp = currentTime;
                _isSent = true;
                peer.SendUserData(_packet);
                return true;
            }

            public bool NeedsSend(long currentTime, NetPeer peer)
            {
                if (_packet == null)
                    return false;

                if (_isSent) //check send time
                {
                    if (!NeedsResend(currentTime, peer))
                        return false;
                }

                return true;
            }

            public bool NeedsResend(long currentTime, NetPeer peer, bool logResend = false)
            {
                if (!_isSent)
                    throw new InvalidOperationException("Cannot query packets that were not sent for resend");

                double resendDelay = peer.ResendDelay * TimeSpan.TicksPerMillisecond;
                double packetHoldTime = currentTime - _timeStamp;
                if (packetHoldTime < resendDelay)
                    return false;

                if(logResend)
                    NetDebug.Write($"[RC]Resend: {packetHoldTime} > {resendDelay}");

                return true;
            }

            public bool CanMerge(NetPeer peer)
            {
                if (_packet == null)
                    return false;

                return peer.CanMerge(_packet);
            }

            public bool Clear(NetPeer peer)
            {
                if (_packet != null)
                {
                    peer.RecycleAndDeliver(_packet);
                    _packet = null;
                    return true;
                }
                return false;
            }
        }

        public int CurrentDynamicWindowSize => _dynamicWindowSize;
        public int CurrentPacketsInFlight => _packetsInFlight;

        private readonly NetPacket _outgoingAcks;            //for send acks
        private readonly PendingPacket[] _pendingPackets;    //for unacked packets and duplicates
        private readonly NetPacket[] _receivedPackets;       //for order
        private readonly bool[] _earlyReceived;              //for unordered

        private int _localSeqence;
        private int _remoteSequence;
        private int _localWindowStart;
        private int _remoteWindowStart;

        private bool _mustSendAcks;

        private readonly DeliveryMethod _deliveryMethod;
        private readonly bool _ordered;
        private readonly int _maxWindowSize;
        private const int BitsInByte = 8;
        private readonly byte _id;

        private int _dynamicWindowSize;
        private int _packetsInFlight;

        public ReliableChannel(NetPeer peer, bool ordered, byte id) : base(peer)
        {
            _id = id;
            _maxWindowSize = NetConstants.MaximumWindowSize;
            _dynamicWindowSize = NetConstants.StartingDynamicWindowSize;
            _ordered = ordered;
            _pendingPackets = new PendingPacket[_maxWindowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
                _pendingPackets[i] = new PendingPacket();

            if (_ordered)
            {
                _deliveryMethod = DeliveryMethod.ReliableOrdered;
                _receivedPackets = new NetPacket[_maxWindowSize];
            }
            else
            {
                _deliveryMethod = DeliveryMethod.ReliableUnordered;
                _earlyReceived = new bool[_maxWindowSize];
            }

            _localWindowStart = 0;
            _localSeqence = 0;
            _remoteSequence = 0;
            _remoteWindowStart = 0;
            _outgoingAcks = new NetPacket(PacketProperty.Ack, (_maxWindowSize - 1) / BitsInByte + 2) {ChannelId = id};
        }

        //ProcessAck in packet
        private void ProcessAck(NetPacket packet)
        {
            if (packet.Size != _outgoingAcks.Size)
            {
                NetDebug.Write("[PA]Invalid acks packet size");
                return;
            }

            ushort ackWindowStart = packet.Sequence;
            int windowRel = NetUtils.RelativeSequenceNumber(_localWindowStart, ackWindowStart);
            if (ackWindowStart >= NetConstants.MaxSequence || windowRel < 0)
            {
                NetDebug.Write("[PA]Bad window start");
                return;
            }

            //check relevance
            if (windowRel >= _maxWindowSize)
            {
                NetDebug.Write("[PA]Old acks");
                return;
            }

            byte[] acksData = packet.RawData;
            lock (_pendingPackets)
            {
                for (int pendingSeq = _localWindowStart;
                    pendingSeq != _localSeqence;
                    pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    int rel = NetUtils.RelativeSequenceNumber(pendingSeq, ackWindowStart);
                    if (rel >= _maxWindowSize)
                    {
                        NetDebug.Write("[PA]REL: " + rel);
                        break;
                    }

                    int pendingIdx = pendingSeq % _maxWindowSize;
                    int currentByte = NetConstants.ChanneledHeaderSize + pendingIdx / BitsInByte;
                    int currentBit = pendingIdx % BitsInByte;
                    if ((acksData[currentByte] & (1 << currentBit)) == 0)
                    {
                        if (Peer.NetManager.EnableStatistics)
                        {
                            Peer.Statistics.IncrementPacketLoss();
                            Peer.NetManager.Statistics.IncrementPacketLoss();
                        }

                        //Skip false ack
                        NetDebug.Write($"[PA]False ack: {pendingSeq}");
                        continue;
                    }

                    if (pendingSeq == _localWindowStart)
                    {
                        //Move window
                        _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;
                    }

                    //clear packet
                    if (_pendingPackets[pendingIdx].Clear(Peer))
                    {
                        if (--_packetsInFlight < 0)
                            throw new InvalidOperationException("Packets in flight dropped below 0, this indicates an error in packet counting");

                        NetDebug.Write($"[PA]Removing reliableInOrder ack: {pendingSeq} - true");
                    }
                }
            }
        }

        protected override bool SendNextPackets()
        {
            if (_mustSendAcks)
            {
                _mustSendAcks = false;
                NetDebug.Write("[RR]SendAcks");
                lock(_outgoingAcks)
                    Peer.SendUserData(_outgoingAcks);
            }

            long currentTime = DateTime.UtcNow.Ticks;
            bool hasPendingPackets = false;

            lock (_pendingPackets)
            {
                //get packets from queue
                lock (OutgoingQueue)
                {
                    while (OutgoingQueue.Count > 0)
                    {
                        int relate = NetUtils.RelativeSequenceNumber(_localSeqence, _localWindowStart);
                        if (relate >= _maxWindowSize)
                        {
                            if (Peer.NetManager.EnableStatistics)
                                Peer.Statistics.IncrementWindowWaits();

                            break;
                        }

                        var netPacket = OutgoingQueue.Dequeue();
                        netPacket.Sequence = (ushort) _localSeqence;
                        netPacket.ChannelId = _id;
                        _pendingPackets[_localSeqence % _maxWindowSize].Init(netPacket);
                        _localSeqence = (_localSeqence + 1) % NetConstants.MaxSequence;
                    }
                }

                //send
                for (int pendingSeq = _localWindowStart; pendingSeq != _localSeqence; pendingSeq = (pendingSeq + 1) % NetConstants.MaxSequence)
                {
                    // Please note: TrySend is invoked on a mutable struct, it's important that it's kept as ref var
                    ref var packet = ref _pendingPackets[pendingSeq % _maxWindowSize];

                    if(_packetsInFlight < _dynamicWindowSize)
                    {
                        // We have the capacity, let's just send it!
                        if (packet.TrySend(currentTime, Peer, ref _packetsInFlight))
                            hasPendingPackets = true;
                    }
                    else
                    {
                        // We're out of the dynamic window capacity, but let's see if there is some unsent merged data
                        // We might be able to squeeze in some extra packets to pad the packets along with some other data
                        if (!Peer.HasUnsentData)
                        {
                            // There's more pending packets
                            hasPendingPackets = true;
                            break; // out of luck!
                        }

                        // Let's check if it actually needs to be sent first
                        if (!packet.NeedsSend(currentTime, Peer))
                        {
                            if (packet.IsSent)
                                hasPendingPackets = true;

                            continue;
                        }

                        // If it doesn't fit in, let's skip it
                        if (!packet.CanMerge(Peer))
                        {
                            hasPendingPackets = true;
                            continue;
                        }

                        // It fits in! Let's squeeze it in and send it, even though we're over our window capacity
                        if (packet.TrySend(currentTime, Peer, ref _packetsInFlight))
                            hasPendingPackets = true;
                    }
                }
            }

            return hasPendingPackets || _mustSendAcks || OutgoingQueue.Count > 0;
        }

        //Process incoming packet
        public override bool ProcessPacket(NetPacket packet)
        {
            if (packet.Property == PacketProperty.Ack)
            {
                ProcessAck(packet);
                return false;
            }
            int seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            int relate = NetUtils.RelativeSequenceNumber(seq, _remoteWindowStart);
            int relateSeq = NetUtils.RelativeSequenceNumber(seq, _remoteSequence);

            if (relateSeq > _maxWindowSize)
            {
                NetDebug.Write("[RR]Bad sequence");
                return false;
            }

            //Drop bad packets
            if (relate < 0)
            {
                //Too old packet doesn't ack
                NetDebug.Write("[RR]ReliableInOrder too old");
                return false;
            }
            if (relate >= _maxWindowSize * 2)
            {
                //Some very new packet
                NetDebug.Write("[RR]ReliableInOrder too new");
                return false;
            }

            //If very new - move window
            int ackIdx;
            int ackByte;
            int ackBit;
            lock (_outgoingAcks)
            {
                if (relate >= _maxWindowSize)
                {
                    //New window position
                    int newWindowStart = (_remoteWindowStart + relate - _maxWindowSize + 1) % NetConstants.MaxSequence;
                    _outgoingAcks.Sequence = (ushort) newWindowStart;

                    //Clean old data
                    while (_remoteWindowStart != newWindowStart)
                    {
                        ackIdx = _remoteWindowStart % _maxWindowSize;
                        ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                        ackBit = ackIdx % BitsInByte;
                        _outgoingAcks.RawData[ackByte] &= (byte) ~(1 << ackBit);
                        _remoteWindowStart = (_remoteWindowStart + 1) % NetConstants.MaxSequence;
                    }
                }

                //Final stage - process valid packet
                //trigger acks send
                _mustSendAcks = true;

                ackIdx = seq % _maxWindowSize;
                ackByte = NetConstants.ChanneledHeaderSize + ackIdx / BitsInByte;
                ackBit = ackIdx % BitsInByte;
                if ((_outgoingAcks.RawData[ackByte] & (1 << ackBit)) != 0)
                {
                    NetDebug.Write("[RR]ReliableInOrder duplicate");
                    //because _mustSendAcks == true
                    AddToPeerChannelSendQueue();
                    return false;
                }

                //save ack
                _outgoingAcks.RawData[ackByte] |= (byte) (1 << ackBit);
            }

            AddToPeerChannelSendQueue();

            //detailed check
            if (seq == _remoteSequence)
            {
                NetDebug.Write("[RR]ReliableInOrder packet success");
                Peer.AddReliablePacket(_deliveryMethod, packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetPacket p;
                    while ((p = _receivedPackets[_remoteSequence % _maxWindowSize]) != null)
                    {
                        //process holden packet
                        _receivedPackets[_remoteSequence % _maxWindowSize] = null;
                        Peer.AddReliablePacket(_deliveryMethod, p);
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % _maxWindowSize])
                    {
                        //process early packet
                        _earlyReceived[_remoteSequence % _maxWindowSize] = false;
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                return true;
            }

            //holden packet
            if (_ordered)
            {
                _receivedPackets[ackIdx] = packet;
            }
            else
            {
                _earlyReceived[ackIdx] = true;
                Peer.AddReliablePacket(_deliveryMethod, packet);
            }
            return true;
        }
    }
}
