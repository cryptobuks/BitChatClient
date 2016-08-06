﻿/*
Technitium Bit Chat
Copyright (C) 2016  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TechnitiumLibrary.IO;

namespace BitChatClient.Network.Connections
{
    delegate void BitChatNetworkInvitation(BinaryID networkID, IPEndPoint peerEP, string message);
    delegate void BitChatNetworkChannelRequest(Connection connection, BinaryID channelName, Stream channel);
    delegate void TcpRelayPeersAvailable(Connection viaConnection, BinaryID channelName, List<IPEndPoint> peerEPs);
    delegate void DhtPacketData(Connection viaConnection, byte[] dhtPacketData);

    enum SignalType : byte
    {
        NOOP = 0,
        ConnectChannelBitChatNetwork = 1,
        DataChannelBitChatNetwork = 2,
        DisconnectChannelBitChatNetwork = 3,
        ConnectChannelProxyTunnel = 4,
        DataChannelProxyTunnel = 5,
        DisconnectChannelProxyTunnel = 6,
        ConnectChannelVirtualConnection = 7,
        DataChannelVirtualConnection = 8,
        DisconnectChannelVirtualConnection = 9,
        PeerStatusQuery = 10,
        PeerStatusAvailable = 11,
        StartTcpRelay = 12,
        StopTcpRelay = 13,
        TcpRelayResponseSuccess = 14,
        TcpRelayResponsePeerList = 15,
        DhtPacketData = 16,
        BitChatNetworkInvitation = 17
    }

    enum ChannelType : byte
    {
        BitChatNetwork = 1,
        ProxyTunnel = 2,
        VirtualConnection = 3
    }

    class Connection : IDisposable
    {
        #region events

        public event BitChatNetworkInvitation BitChatNetworkInvitation;
        public event BitChatNetworkChannelRequest BitChatNetworkChannelRequest;
        public event TcpRelayPeersAvailable TcpRelayPeersAvailable;
        public event EventHandler Disposed;

        #endregion

        #region variables

        const int MAX_FRAME_SIZE = 65279; //65535 (max ipv4 packet size) - 256 (margin for other headers)
        const int BUFFER_SIZE = 65535;

        Stream _baseStream;
        BinaryID _remotePeerID;
        IPEndPoint _remotePeerEP;
        ConnectionManager _connectionManager;

        Dictionary<BinaryID, ChannelStream> _bitChatNetworkChannels = new Dictionary<BinaryID, ChannelStream>();
        Dictionary<BinaryID, ChannelStream> _proxyTunnelChannels = new Dictionary<BinaryID, ChannelStream>();
        Dictionary<BinaryID, ChannelStream> _virtualConnectionChannels = new Dictionary<BinaryID, ChannelStream>();

        Thread _readThread;

        Dictionary<BinaryID, object> _peerStatusLockList = new Dictionary<BinaryID, object>();
        List<Joint> _proxyTunnelJointList = new List<Joint>();

        Dictionary<BinaryID, object> _tcpRelayRequestLockList = new Dictionary<BinaryID, object>();
        Dictionary<BinaryID, TcpRelayService> _tcpRelays = new Dictionary<BinaryID, TcpRelayService>();

        int _channelWriteTimeout = 30000;

        byte[] _writeBufferData = new byte[BUFFER_SIZE];

        #endregion

        #region constructor

        public Connection(Stream baseStream, BinaryID remotePeerID, IPEndPoint remotePeerEP, ConnectionManager connectionManager)
        {
            _baseStream = baseStream;
            _remotePeerID = remotePeerID;
            _remotePeerEP = remotePeerEP;
            _connectionManager = connectionManager;
        }

        #endregion

        #region IDisposable support

        ~Connection()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            lock (this)
            {
                if (!_disposed)
                {
                    //stop read thread
                    try
                    {
                        if (_readThread != null)
                            _readThread.Abort();
                    }
                    catch
                    { }

                    //dispose all channels
                    List<ChannelStream> streamList = new List<ChannelStream>();

                    lock (_bitChatNetworkChannels)
                    {
                        foreach (KeyValuePair<BinaryID, ChannelStream> channel in _bitChatNetworkChannels)
                            streamList.Add(channel.Value);
                    }

                    lock (_proxyTunnelChannels)
                    {
                        foreach (KeyValuePair<BinaryID, ChannelStream> channel in _proxyTunnelChannels)
                            streamList.Add(channel.Value);
                    }

                    lock (_virtualConnectionChannels)
                    {
                        foreach (KeyValuePair<BinaryID, ChannelStream> channel in _virtualConnectionChannels)
                            streamList.Add(channel.Value);
                    }

                    foreach (ChannelStream stream in streamList)
                    {
                        try
                        {
                            stream.Dispose();
                        }
                        catch
                        { }
                    }

                    //remove this connection from tcp relays
                    lock (_tcpRelays)
                    {
                        foreach (TcpRelayService relay in _tcpRelays.Values)
                        {
                            relay.StopTcpRelay(this);
                        }

                        _tcpRelays.Clear();
                    }

                    //dispose base stream
                    lock (_baseStream)
                    {
                        try
                        {
                            _baseStream.Dispose();
                        }
                        catch
                        { }
                    }
                    
                    _disposed = true;

                    Disposed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion

        #region private

        private void WriteFrame(SignalType signalType, BinaryID channelName, byte[] buffer, int offset, int count)
        {
            const int FRAME_HEADER_SIZE = 23;
            int frameCount = MAX_FRAME_SIZE - FRAME_HEADER_SIZE;

            lock (_baseStream)
            {
                do
                {
                    if (count < frameCount)
                        frameCount = count;

                    //write frame signal
                    _writeBufferData[0] = (byte)signalType;

                    //write channel name
                    Buffer.BlockCopy(channelName.ID, 0, _writeBufferData, 1, 20);

                    //write data length
                    byte[] bufferCount = BitConverter.GetBytes(Convert.ToUInt16(frameCount));
                    _writeBufferData[21] = bufferCount[0];
                    _writeBufferData[22] = bufferCount[1];

                    //write data
                    if (frameCount > 0)
                        Buffer.BlockCopy(buffer, offset, _writeBufferData, FRAME_HEADER_SIZE, frameCount);

                    //output buffer to base stream
                    _baseStream.Write(_writeBufferData, 0, FRAME_HEADER_SIZE + frameCount);
                    _baseStream.Flush();

                    offset += frameCount;
                    count -= frameCount;
                }
                while (count > 0);
            }
        }

        private void ReadFrameAsync()
        {
            try
            {
                //frame parameters
                SignalType signalType;
                BinaryID channelName = new BinaryID(new byte[20]);
                int dataLength;
                byte[] dataBuffer = new byte[BUFFER_SIZE];

                while (true)
                {
                    #region Read frame from base stream

                    //read frame signal
                    signalType = (SignalType)_baseStream.ReadByte();

                    //read channel name
                    OffsetStream.StreamRead(_baseStream, channelName.ID, 0, 20);

                    //read data length
                    OffsetStream.StreamRead(_baseStream, dataBuffer, 0, 2);
                    dataLength = BitConverter.ToUInt16(dataBuffer, 0);

                    //read data
                    if (dataLength > 0)
                        OffsetStream.StreamRead(_baseStream, dataBuffer, 0, dataLength);

                    #endregion

                    switch (signalType)
                    {
                        case SignalType.NOOP:
                            break;

                        case SignalType.ConnectChannelBitChatNetwork:
                            #region ConnectChannelBitChatNetwork
                            {
                                ChannelStream channel = null;

                                try
                                {
                                    channel = new ChannelStream(this, channelName, ChannelType.BitChatNetwork);

                                    lock (_bitChatNetworkChannels)
                                    {
                                        _bitChatNetworkChannels.Add(channelName, channel);
                                    }

                                    BitChatNetworkChannelRequest?.BeginInvoke(this, channelName.Clone(), channel, null, null);
                                }
                                catch
                                {
                                    if (channel != null)
                                        channel.Dispose();
                                }

                                //check if tcp relay is hosted for the channel. reply back tcp relay peers list if available
                                try
                                {
                                    List<IPEndPoint> peerEPs = TcpRelayService.GetPeerEPs(channelName, this);

                                    if ((peerEPs != null) && (peerEPs.Count > 0))
                                    {
                                        using (MemoryStream mS = new MemoryStream(128))
                                        {
                                            mS.WriteByte(Convert.ToByte(peerEPs.Count));

                                            foreach (IPEndPoint peerEP in peerEPs)
                                            {
                                                IPEndPointParser.WriteTo(peerEP, mS);
                                            }

                                            byte[] data = mS.ToArray();

                                            WriteFrame(SignalType.TcpRelayResponsePeerList, channelName, data, 0, data.Length);
                                        }
                                    }
                                }
                                catch
                                { }
                            }

                            #endregion
                            break;

                        case SignalType.DataChannelBitChatNetwork:
                            #region DataChannelBitChatNetwork
                            {
                                ChannelStream channel = null;

                                try
                                {
                                    lock (_bitChatNetworkChannels)
                                    {
                                        channel = _bitChatNetworkChannels[channelName];
                                    }

                                    channel.WriteBuffer(dataBuffer, 0, dataLength, _channelWriteTimeout);
                                }
                                catch
                                {
                                    if (channel != null)
                                        channel.Dispose();
                                }
                            }

                            #endregion
                            break;

                        case SignalType.DisconnectChannelBitChatNetwork:
                            #region DisconnectChannelBitChatNetwork

                            try
                            {
                                lock (_bitChatNetworkChannels)
                                {
                                    _bitChatNetworkChannels[channelName].Dispose();
                                }
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.ConnectChannelProxyTunnel:
                            #region ConnectChannelProxyTunnel
                            {
                                ChannelStream remoteChannel1 = null;
                                Stream remoteChannel2 = null;

                                try
                                {
                                    //get remote peer ep
                                    IPEndPoint tunnelToPeerEP = ConvertChannelNameToEp(channelName);

                                    //add first stream into list
                                    remoteChannel1 = new ChannelStream(this, channelName, ChannelType.ProxyTunnel);

                                    lock (_proxyTunnelChannels)
                                    {
                                        _proxyTunnelChannels.Add(channelName, remoteChannel1);
                                    }

                                    //get remote channel service
                                    Connection remotePeerConnection = _connectionManager.GetExistingConnection(tunnelToPeerEP);

                                    //get remote stream for virtual connection
                                    remoteChannel2 = remotePeerConnection.RequestVirtualConnectionChannel(_remotePeerEP);

                                    //join current and remote stream
                                    Joint joint = new Joint(remoteChannel1, remoteChannel2);
                                    joint.Disposed += joint_Disposed;

                                    lock (_proxyTunnelJointList)
                                    {
                                        _proxyTunnelJointList.Add(joint);
                                    }

                                    joint.Start();
                                }
                                catch
                                {
                                    if (remoteChannel1 != null)
                                        remoteChannel1.Dispose();

                                    if (remoteChannel2 != null)
                                        remoteChannel2.Dispose();
                                }
                            }

                            #endregion
                            break;

                        case SignalType.DataChannelProxyTunnel:
                            #region DataChannelProxyTunnel
                            {
                                ChannelStream channel = null;

                                try
                                {
                                    lock (_proxyTunnelChannels)
                                    {
                                        channel = _proxyTunnelChannels[channelName];
                                    }

                                    channel.WriteBuffer(dataBuffer, 0, dataLength, _channelWriteTimeout);
                                }
                                catch
                                {
                                    if (channel != null)
                                        channel.Dispose();
                                }
                            }
                            #endregion  
                            break;

                        case SignalType.DisconnectChannelProxyTunnel:
                            #region DisconnectChannelProxyTunnel

                            try
                            {
                                lock (_proxyTunnelChannels)
                                {
                                    _proxyTunnelChannels[channelName].Dispose();
                                }
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.ConnectChannelVirtualConnection:
                            #region ConnectChannelVirtualConnection
                            {
                                ChannelStream channel = null;

                                try
                                {
                                    //add current stream into list
                                    channel = new ChannelStream(this, channelName, ChannelType.VirtualConnection);

                                    lock (_virtualConnectionChannels)
                                    {
                                        _virtualConnectionChannels.Add(channelName, channel);
                                    }

                                    IPEndPoint virtualRemotePeerEP = ConvertChannelNameToEp(channelName);

                                    //pass channel as virtual connection async
                                    ThreadPool.QueueUserWorkItem(AcceptVirtualConnectionAsync, new object[] { channel, virtualRemotePeerEP });
                                }
                                catch
                                {
                                    if (channel != null)
                                        channel.Dispose();
                                }
                            }

                            #endregion
                            break;

                        case SignalType.DataChannelVirtualConnection:
                            #region DataChannelVirtualConnection
                            {
                                ChannelStream channel = null;

                                try
                                {
                                    lock (_virtualConnectionChannels)
                                    {
                                        channel = _virtualConnectionChannels[channelName];
                                    }

                                    channel.WriteBuffer(dataBuffer, 0, dataLength, _channelWriteTimeout);
                                }
                                catch
                                {
                                    if (channel != null)
                                        channel.Dispose();
                                }
                            }
                            #endregion
                            break;

                        case SignalType.DisconnectChannelVirtualConnection:
                            #region DisconnectChannelVirtualConnection

                            try
                            {
                                lock (_virtualConnectionChannels)
                                {
                                    _virtualConnectionChannels[channelName].Dispose();
                                }
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.PeerStatusQuery:
                            #region PeerStatusQuery

                            try
                            {
                                if (_connectionManager.IsPeerConnectionAvailable(ConvertChannelNameToEp(channelName)))
                                    WriteFrame(SignalType.PeerStatusAvailable, channelName, null, 0, 0);
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.PeerStatusAvailable:
                            #region PeerStatusAvailable

                            try
                            {
                                lock (_peerStatusLockList)
                                {
                                    object lockObject = _peerStatusLockList[channelName];

                                    lock (lockObject)
                                    {
                                        Monitor.Pulse(lockObject);
                                    }
                                }
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.StartTcpRelay:
                            #region StartTcpRelay
                            {
                                BinaryID[] networkIDs;
                                Uri[] trackerURIs;

                                using (MemoryStream mS = new MemoryStream(dataBuffer, 0, dataLength, false))
                                {
                                    //read network id list
                                    networkIDs = new BinaryID[mS.ReadByte()];
                                    byte[] XORnetworkID = new byte[20];

                                    for (int i = 0; i < networkIDs.Length; i++)
                                    {
                                        mS.Read(XORnetworkID, 0, 20);

                                        byte[] networkID = new byte[20];

                                        for (int j = 0; j < 20; j++)
                                        {
                                            networkID[j] = (byte)(channelName.ID[j] ^ XORnetworkID[j]);
                                        }

                                        networkIDs[i] = new BinaryID(networkID);
                                    }

                                    //read tracker uri list
                                    trackerURIs = new Uri[mS.ReadByte()];
                                    byte[] data = new byte[255];

                                    for (int i = 0; i < trackerURIs.Length; i++)
                                    {
                                        int length = mS.ReadByte();
                                        mS.Read(data, 0, length);

                                        trackerURIs[i] = new Uri(Encoding.UTF8.GetString(data, 0, length));
                                    }
                                }

                                lock (_tcpRelays)
                                {
                                    foreach (BinaryID networkID in networkIDs)
                                    {
                                        if (!_tcpRelays.ContainsKey(networkID))
                                        {
                                            TcpRelayService relay = TcpRelayService.StartTcpRelay(networkID, this, _connectionManager.LocalPort, _connectionManager.DhtClient, trackerURIs);
                                            _tcpRelays.Add(networkID, relay);
                                        }
                                    }
                                }

                                WriteFrame(SignalType.TcpRelayResponseSuccess, channelName, null, 0, 0);
                            }

                            #endregion
                            break;

                        case SignalType.StopTcpRelay:
                            #region StopTcpRelay
                            {
                                BinaryID[] networkIDs;

                                using (MemoryStream mS = new MemoryStream(dataBuffer, 0, dataLength, false))
                                {
                                    //read network id list
                                    networkIDs = new BinaryID[mS.ReadByte()];
                                    byte[] XORnetworkID = new byte[20];

                                    for (int i = 0; i < networkIDs.Length; i++)
                                    {
                                        mS.Read(XORnetworkID, 0, 20);

                                        byte[] networkID = new byte[20];

                                        for (int j = 0; j < 20; j++)
                                        {
                                            networkID[j] = (byte)(channelName.ID[j] ^ XORnetworkID[j]);
                                        }

                                        networkIDs[i] = new BinaryID(networkID);
                                    }
                                }

                                lock (_tcpRelays)
                                {
                                    foreach (BinaryID networkID in networkIDs)
                                    {
                                        if (_tcpRelays.ContainsKey(networkID))
                                        {
                                            _tcpRelays[networkID].StopTcpRelay(this);
                                            _tcpRelays.Remove(networkID);
                                        }
                                    }
                                }

                                WriteFrame(SignalType.TcpRelayResponseSuccess, channelName, null, 0, 0);
                            }

                            #endregion
                            break;

                        case SignalType.TcpRelayResponseSuccess:
                            #region TcpRelayResponseSuccess

                            try
                            {
                                lock (_tcpRelayRequestLockList)
                                {
                                    object lockObject = _tcpRelayRequestLockList[channelName];

                                    lock (lockObject)
                                    {
                                        Monitor.Pulse(lockObject);
                                    }
                                }
                            }
                            catch
                            { }

                            #endregion
                            break;

                        case SignalType.TcpRelayResponsePeerList:
                            #region TcpRelayResponsePeerList

                            using (MemoryStream mS = new MemoryStream(dataBuffer, 0, dataLength, false))
                            {
                                int count = mS.ReadByte();
                                List<IPEndPoint> peerEPs = new List<IPEndPoint>(count);

                                for (int i = 0; i < count; i++)
                                {
                                    peerEPs.Add(IPEndPointParser.Parse(mS));
                                }

                                TcpRelayPeersAvailable?.BeginInvoke(this, channelName.Clone(), peerEPs, null, null);
                            }

                            #endregion
                            break;

                        case SignalType.DhtPacketData:
                            #region DhtPacketData

                            _connectionManager.DhtClient.ProcessPacket(dataBuffer, 0, dataLength, _remotePeerEP.Address);

                            #endregion
                            break;

                        case SignalType.BitChatNetworkInvitation:
                            #region ChannelInvitationBitChatNetwork

                            BitChatNetworkInvitation?.BeginInvoke(channelName.Clone(), _remotePeerEP, Encoding.UTF8.GetString(dataBuffer, 0, dataLength), null, null);

                            #endregion
                            break;

                        default:
                            throw new IOException("Invalid frame signal type.");
                    }
                }
            }
            catch
            { }
            finally
            {
                Dispose();
            }
        }

        private void AcceptVirtualConnectionAsync(object state)
        {
            object[] parameters = state as object[];

            ChannelStream channel = parameters[0] as ChannelStream;
            IPEndPoint virtualRemotePeerEP = parameters[1] as IPEndPoint;

            try
            {
                Connection connection = _connectionManager.AcceptConnectionInitiateProtocol(channel, virtualRemotePeerEP);
            }
            catch
            {
                try
                {
                    channel.Dispose();
                }
                catch
                { }
            }
        }

        private void joint_Disposed(object sender, EventArgs e)
        {
            lock (_proxyTunnelJointList)
            {
                _proxyTunnelJointList.Remove(sender as Joint);
            }
        }

        private BinaryID ConvertEpToChannelName(IPEndPoint ep)
        {
            byte[] channelName = new byte[20];

            byte[] address = ep.Address.GetAddressBytes();
            byte[] port = BitConverter.GetBytes(Convert.ToUInt16(ep.Port));

            switch (ep.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    channelName[0] = 0;
                    break;

                case AddressFamily.InterNetworkV6:
                    channelName[0] = 1;
                    break;

                default:
                    throw new Exception("AddressFamily not supported.");
            }

            Buffer.BlockCopy(address, 0, channelName, 1, address.Length);
            Buffer.BlockCopy(port, 0, channelName, 1 + address.Length, 2);

            return new BinaryID(channelName);
        }

        private IPEndPoint ConvertChannelNameToEp(BinaryID channelName)
        {
            byte[] address;
            byte[] port;

            switch (channelName.ID[0])
            {
                case 0:
                    address = new byte[4];
                    port = new byte[2];
                    Buffer.BlockCopy(channelName.ID, 1, address, 0, 4);
                    Buffer.BlockCopy(channelName.ID, 1 + 4, port, 0, 2);
                    break;

                case 1:
                    address = new byte[16];
                    port = new byte[2];
                    Buffer.BlockCopy(channelName.ID, 1, address, 0, 16);
                    Buffer.BlockCopy(channelName.ID, 1 + 16, port, 0, 2);
                    break;

                default:
                    throw new Exception("AddressFamily not supported.");
            }

            return new IPEndPoint(new IPAddress(address), BitConverter.ToUInt16(port, 0));
        }

        private Stream RequestVirtualConnectionChannel(IPEndPoint forPeerEP)
        {
            BinaryID channelName = ConvertEpToChannelName(forPeerEP);
            ChannelStream channel = new ChannelStream(this, channelName, ChannelType.VirtualConnection);

            lock (_virtualConnectionChannels)
            {
                _virtualConnectionChannels.Add(channelName, channel);
            }

            //send signal
            WriteFrame(SignalType.ConnectChannelVirtualConnection, channelName, null, 0, 0);

            return channel;
        }

        #endregion

        #region static

        public static BinaryID GetChannelName(BinaryID localPeerID, BinaryID remotePeerID, BinaryID networkID)
        {
            // this is done to avoid disclosing networkID to passive network sniffing
            // channelName = hmac( localPeerID XOR remotePeerID, networkID)

            using (HMACSHA1 hmacSHA1 = new HMACSHA1(networkID.ID))
            {
                return new BinaryID(hmacSHA1.ComputeHash((localPeerID ^ remotePeerID).ID));
            }
        }

        public static bool IsVirtualConnection(Stream stream)
        {
            return (stream.GetType() == typeof(ChannelStream));
        }

        #endregion

        #region public

        public void Start()
        {
            if (_readThread == null)
            {
                _readThread = new Thread(ReadFrameAsync);
                _readThread.IsBackground = true;
                _readThread.Start();
            }
        }

        public Stream RequestBitChatNetworkChannel(BinaryID channelName)
        {
            ChannelStream channel = new ChannelStream(this, channelName, ChannelType.BitChatNetwork);

            lock (_bitChatNetworkChannels)
            {
                _bitChatNetworkChannels.Add(channelName, channel);
            }

            //send connect signal
            WriteFrame(SignalType.ConnectChannelBitChatNetwork, channelName, null, 0, 0);

            return channel;
        }

        public bool BitChatNetworkChannelExists(BinaryID channelName)
        {
            lock (_bitChatNetworkChannels)
            {
                return _bitChatNetworkChannels.ContainsKey(channelName);
            }
        }

        public bool RequestPeerStatus(IPEndPoint remotePeerEP)
        {
            BinaryID channelName = ConvertEpToChannelName(remotePeerEP);
            object lockObject = new object();

            lock (_peerStatusLockList)
            {
                _peerStatusLockList.Add(channelName, lockObject);
            }

            try
            {
                lock (lockObject)
                {
                    WriteFrame(SignalType.PeerStatusQuery, channelName, null, 0, 0);

                    return Monitor.Wait(lockObject, 10000);
                }
            }
            finally
            {
                lock (_peerStatusLockList)
                {
                    _peerStatusLockList.Remove(channelName);
                }
            }
        }

        public Stream RequestProxyTunnelChannel(IPEndPoint remotePeerEP)
        {
            BinaryID channelName = ConvertEpToChannelName(remotePeerEP);
            ChannelStream channel = new ChannelStream(this, channelName, ChannelType.ProxyTunnel);

            lock (_proxyTunnelChannels)
            {
                _proxyTunnelChannels.Add(channelName, channel);
            }

            //send signal
            WriteFrame(SignalType.ConnectChannelProxyTunnel, channelName, null, 0, 0);

            return channel;
        }

        public bool RequestStartTcpRelay(BinaryID[] networkIDs, Uri[] trackerURIs)
        {
            BinaryID channelName = BinaryID.GenerateRandomID160();
            object lockObject = new object();

            lock (_tcpRelayRequestLockList)
            {
                _tcpRelayRequestLockList.Add(channelName, lockObject);
            }

            try
            {
                lock (lockObject)
                {
                    using (MemoryStream mS = new MemoryStream(1024))
                    {
                        byte[] XORnetworkID = new byte[20];
                        byte[] randomChannelID = channelName.ID;

                        //write networkid list
                        mS.WriteByte(Convert.ToByte(networkIDs.Length));

                        foreach (BinaryID networkID in networkIDs)
                        {
                            byte[] network = networkID.ID;

                            for (int i = 0; i < 20; i++)
                            {
                                XORnetworkID[i] = (byte)(randomChannelID[i] ^ network[i]);
                            }

                            mS.Write(XORnetworkID, 0, 20);
                        }

                        //write tracker uri list
                        mS.WriteByte(Convert.ToByte(trackerURIs.Length));

                        foreach (Uri trackerURI in trackerURIs)
                        {
                            byte[] buffer = Encoding.UTF8.GetBytes(trackerURI.AbsoluteUri);
                            mS.WriteByte(Convert.ToByte(buffer.Length));
                            mS.Write(buffer, 0, buffer.Length);
                        }

                        byte[] data = mS.ToArray();

                        WriteFrame(SignalType.StartTcpRelay, channelName, data, 0, data.Length);
                    }

                    return Monitor.Wait(lockObject, 120000);
                }
            }
            finally
            {
                lock (_tcpRelayRequestLockList)
                {
                    _tcpRelayRequestLockList.Remove(channelName);
                }
            }
        }

        public bool RequestStopTcpRelay(BinaryID[] networkIDs)
        {
            BinaryID channelName = BinaryID.GenerateRandomID160();
            object lockObject = new object();

            lock (_tcpRelayRequestLockList)
            {
                _tcpRelayRequestLockList.Add(channelName, lockObject);
            }

            try
            {
                lock (lockObject)
                {
                    using (MemoryStream mS = new MemoryStream(1024))
                    {
                        byte[] XORnetworkID = new byte[20];
                        byte[] randomChannelID = channelName.ID;

                        //write networkid list
                        mS.WriteByte(Convert.ToByte(networkIDs.Length));

                        foreach (BinaryID networkID in networkIDs)
                        {
                            byte[] network = networkID.ID;

                            for (int i = 0; i < 20; i++)
                            {
                                XORnetworkID[i] = (byte)(randomChannelID[i] ^ network[i]);
                            }

                            mS.Write(XORnetworkID, 0, 20);
                        }

                        byte[] data = mS.ToArray();

                        WriteFrame(SignalType.StopTcpRelay, channelName, data, 0, data.Length);
                    }

                    return Monitor.Wait(lockObject, 10000);
                }
            }
            finally
            {
                lock (_tcpRelayRequestLockList)
                {
                    _tcpRelayRequestLockList.Remove(channelName);
                }
            }
        }

        public void SendNOOP()
        {
            WriteFrame(SignalType.NOOP, BinaryID.GenerateRandomID160(), null, 0, 0);
        }

        public void SendDhtPacket(byte[] buffer, int offset, int count)
        {
            WriteFrame(SignalType.DhtPacketData, BinaryID.GenerateRandomID160(), buffer, offset, count);
        }

        public void SendBitChatNetworkInvitation(BinaryID networkID, string message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);

            //send invitation signal with message
            WriteFrame(SignalType.BitChatNetworkInvitation, networkID, buffer, 0, buffer.Length);
        }

        #endregion

        #region properties

        public BinaryID LocalPeerID
        { get { return _connectionManager.LocalPeerID; } }

        public BinaryID RemotePeerID
        { get { return _remotePeerID; } }

        public IPEndPoint RemotePeerEP
        { get { return _remotePeerEP; } }

        public int ChannelWriteTimeout
        {
            get { return _channelWriteTimeout; }
            set { _channelWriteTimeout = value; }
        }

        public bool IsVirtual
        { get { return (_baseStream.GetType() == typeof(ChannelStream)); } }

        #endregion

        private class ChannelStream : Stream
        {
            #region variables

            const int CHANNEL_READ_TIMEOUT = 30000; //channel timeout 30 sec; application must NOOP
            const int CHANNEL_WRITE_TIMEOUT = 30000; //dummy timeout for write since base channel write timeout will be used

            Connection _connection;
            BinaryID _channelName;
            ChannelType _channelType;

            readonly byte[] _buffer = new byte[BUFFER_SIZE];
            int _offset;
            int _count;

            int _readTimeout = CHANNEL_READ_TIMEOUT;
            int _writeTimeout = CHANNEL_WRITE_TIMEOUT;

            #endregion

            #region constructor

            public ChannelStream(Connection connection, BinaryID channelName, ChannelType channelType)
            {
                _connection = connection;
                _channelName = channelName.Clone();
                _channelType = channelType;
            }

            #endregion

            #region IDisposable

            bool _disposed = false;

            protected override void Dispose(bool disposing)
            {
                lock (this)
                {
                    if (!_disposed)
                    {
                        switch (_channelType)
                        {
                            case ChannelType.BitChatNetwork:
                                lock (_connection._bitChatNetworkChannels)
                                {
                                    _connection._bitChatNetworkChannels.Remove(_channelName);
                                }

                                try
                                {
                                    //send disconnect signal
                                    _connection.WriteFrame(SignalType.DisconnectChannelBitChatNetwork, _channelName, null, 0, 0);
                                }
                                catch
                                { }
                                break;

                            case ChannelType.ProxyTunnel:
                                lock (_connection._proxyTunnelChannels)
                                {
                                    _connection._proxyTunnelChannels.Remove(_channelName);
                                }

                                try
                                {
                                    //send disconnect signal
                                    _connection.WriteFrame(SignalType.DisconnectChannelProxyTunnel, _channelName, null, 0, 0);
                                }
                                catch
                                { }
                                break;

                            case ChannelType.VirtualConnection:
                                lock (_connection._virtualConnectionChannels)
                                {
                                    _connection._virtualConnectionChannels.Remove(_channelName);
                                }

                                try
                                {
                                    //send disconnect signal
                                    _connection.WriteFrame(SignalType.DisconnectChannelVirtualConnection, _channelName, null, 0, 0);
                                }
                                catch
                                { }
                                break;
                        }

                        Monitor.PulseAll(this);

                        _disposed = true;
                    }
                }
            }

            #endregion

            #region stream support

            public override bool CanRead
            {
                get { return _connection._baseStream.CanRead; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return _connection._baseStream.CanWrite; }
            }

            public override bool CanTimeout
            {
                get { return true; }
            }

            public override int ReadTimeout
            {
                get { return _readTimeout; }
                set { _readTimeout = value; }
            }

            public override int WriteTimeout
            {
                get { return _writeTimeout; }
                set { _writeTimeout = value; }
            }

            public override void Flush()
            {
                //do nothing
            }

            public override long Length
            {
                get { throw new IOException("ChannelStream stream does not support seeking."); }
            }

            public override long Position
            {
                get
                {
                    throw new IOException("ChannelStream stream does not support seeking.");
                }
                set
                {
                    throw new IOException("ChannelStream stream does not support seeking.");
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new IOException("ChannelStream stream does not support seeking.");
            }

            public override void SetLength(long value)
            {
                throw new IOException("ChannelStream stream does not support seeking.");
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count < 1)
                    throw new IOException("Count must be atleast 1 byte.");

                lock (this)
                {
                    if (_disposed)
                        throw new IOException("Cannot read from a closed stream.");

                    if (_count < 1)
                    {
                        if (!Monitor.Wait(this, _readTimeout))
                            throw new IOException("Read timed out.");

                        if (_count < 1)
                            return 0;
                    }

                    int bytesToCopy = count;

                    if (bytesToCopy > _count)
                        bytesToCopy = _count;

                    Buffer.BlockCopy(_buffer, _offset, buffer, offset, bytesToCopy);

                    _offset += bytesToCopy;
                    _count -= bytesToCopy;

                    if (_count < 1)
                        Monitor.Pulse(this);

                    return bytesToCopy;
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                switch (_channelType)
                {
                    case ChannelType.BitChatNetwork:
                        _connection.WriteFrame(SignalType.DataChannelBitChatNetwork, _channelName, buffer, offset, count);
                        break;

                    case ChannelType.ProxyTunnel:
                        _connection.WriteFrame(SignalType.DataChannelProxyTunnel, _channelName, buffer, offset, count);
                        break;

                    case ChannelType.VirtualConnection:
                        _connection.WriteFrame(SignalType.DataChannelVirtualConnection, _channelName, buffer, offset, count);
                        break;
                }
            }

            #endregion

            #region private

            internal void WriteBuffer(byte[] buffer, int offset, int count, int timeout)
            {
                if (count > 0)
                {
                    lock (this)
                    {
                        if (_disposed)
                            throw new IOException("Cannot write buffer to a closed stream.");

                        if (_count > 0)
                        {
                            if (!Monitor.Wait(this, timeout))
                                throw new IOException("Channel WriteBuffer timed out.");

                            if (_count > 0)
                                throw new IOException("Channel WriteBuffer failed. Buffer not empty.");
                        }

                        Buffer.BlockCopy(buffer, offset, _buffer, 0, count);
                        _offset = 0;
                        _count = count;

                        Monitor.Pulse(this);
                    }
                }
            }

            #endregion
        }
    }
}
