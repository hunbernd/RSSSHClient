﻿using System;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Threading;

using ProtoBuf;

//using Renci.SshNet;
//using Renci.SshNet.Common;

using rsctrl.core;
//using rsctrl.peers;

using Renci.SshNet;

//[HEADER: 16 bytes: 4 x Network Order uint32_t][ VARIABLE LENGTH BODY ] 
//[ MAGIC_CODE ] [ MSG_ID ] [ REQ_ID ] [ BODY_SIZE ] [ ..... BODY ..... ]

//MagicCode = 0x137f0001. will be incremented for new versions of the protocol.
//MsgID = Corresponds to the format of the Body.
//ReqID = Generated by Requester, Returned in Response, make sure its unique. (undefined behaviour for duplicates)
//BodySize = Byte Length of Body.

//The Body will consist of a protobuf encoded message.

namespace Sehraf.RetroShareSSH
{

    public struct RSProtoBuffSSHMsg
    {
        private const uint _magicCode = 0x137f0001;
        public const byte _headerSize = 16;

        private uint _msgID, _reqID, _bodySize;
        Stream _pbMsg;
        //bool _important;

        public uint MagicCode { get { return _magicCode; } }
        public uint MsgID { get { return _msgID; } set { _msgID = value; } }
        public uint ReqID { get { return _reqID; } set { _reqID = value; } }
        public uint BodySize { get { return _bodySize; } set { _bodySize = value; } }
        public Stream ProtoBuffMsg { get { return _pbMsg; } set { _pbMsg = value; } }
        //public bool IsImportant { get { return _important; } set { _important = value; } }
    }

    class PersonComparer : IComparer<Person>
    {
        public int Compare(Person p1, Person p2)
        {
            return p1.name.CompareTo(p2.name);
        }
    }

    class RSProtoBuf
    {
        const bool DEBUG = true;
        //MemoryStream _streamIn, _streamOut;
        ShellStream _stream;
        uint _nextReqID;
        uint _timeOut;
        //long _streamOutPos;
        //long _streamInPos;
        RSRPC _parent;

        Queue<RSProtoBuffSSHMsg> _sendQueue;

        Thread _t;
        bool _run;

        //public RSProtoBuf(MemoryStream streamIn, MemoryStream streamOut, Queue<RSProtoBuffSSHMsg> queue, RSRPC parent, uint timeout = 1000, bool useThread = true)
        public RSProtoBuf(ShellStream stream, Queue<RSProtoBuffSSHMsg> queue, RSRPC parent, uint timeout = 1000, bool useThread = true)
        {
            //_streamIn = streamIn;
            //_streamOut = streamOut;
            _stream = stream;

            _nextReqID = 1;
            _timeOut = timeout;
            _sendQueue = queue;
            _parent = parent;

            //_streamOutPos = 0;
            //_streamInPos = 0;

            if (useThread)
            {
                _run = true;
                _t = new Thread(new ThreadStart(mainLoop));
                _t.Priority = ThreadPriority.AboveNormal;
                _t.Name = "RS Send/Recieve loop";
                _t.Start();
            }
        }

        public uint Send(RSProtoBuffSSHMsg msg)
        {
            System.Diagnostics.Debug.WriteLine("send: sending packet " + msg.ReqID + " MsgID: " + msg.MsgID + " body size: " + msg.BodySize);
            if(_stream.CanWrite)
            {
                _stream.Write(CreatePacketFromMsg(msg), 0, 16 + (int)msg.BodySize);
                _stream.Flush();
            }
            else 
            {
                System.Diagnostics.Debug.WriteLine("send: can't write stream");
                System.Diagnostics.Debug.WriteLine("diconnecting ....");
                _parent.Disconnect(true);
            }

            return msg.ReqID;
        }
        
        public bool Receive(out RSProtoBuffSSHMsg msg, uint timeOut = 0)
        {
            msg = new RSProtoBuffSSHMsg();
            //if (!_streamOut.CanRead) 
            if (!_stream.CanRead)
            {
                System.Diagnostics.Debug.WriteLine("rec: cannot read stream!");
                System.Diagnostics.Debug.WriteLine("disconnecting ....");
                _parent.Disconnect(true);
                return false;
            } 
            if (!ReadMsgFromStream(ref msg, timeOut))
            {
                System.Diagnostics.Debug.WriteLine("rec: Error receiving data from stream");
                return false;
            }

            System.Diagnostics.Debug.WriteLine("rec: " + msg.ReqID + " body Length: " + msg.ProtoBuffMsg.Length);
            return true;
        }
        
        private bool ReadMsgFromStream(ref RSProtoBuffSSHMsg msg, uint timeOut = 0)
        {
            if (timeOut == 0)
                timeOut = _timeOut;

            byte[] input = new byte[16];
            bool done = false;

            // get Header
            DateTime timeOutTime = DateTime.Now.AddMilliseconds(timeOut);
            while (DateTime.Now < timeOutTime)
            {
                if (_stream.DataAvailable)
                {
                    _stream.Read(input, 0, 16); 
                    done = true;
                    break;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("rec: Stream to short (head) ... waiting (" + (DateTime.Now - timeOutTime).Milliseconds + ")");
                    System.Threading.Thread.Sleep(250);
                }
            }
            if (!done)
            {
                System.Diagnostics.Debug.WriteLine("rec: Could not get header");
                return false;
            }

            // read header
            Array.Reverse(input);
            msg.BodySize = BitConverter.ToUInt32(input, 0);
            msg.ReqID = BitConverter.ToUInt32(input, 4);
            msg.MsgID = BitConverter.ToUInt32(input, 8);
            uint magicCode = BitConverter.ToUInt32(input, 12);
            System.Diagnostics.Debug.WriteLine("rec: ReqID: " + msg.ReqID + " - MsgID: " + msg.MsgID + " - body size: " + msg.BodySize + " byte");

            if (magicCode != msg.MagicCode)
            {
                System.Diagnostics.Debug.WriteLine("rec: MagicCode mismatch -> returning");
                return false;
            }
            if (msg.BodySize > 1000)
                Thread.Sleep(500);

            // get ProtoBufMsg
            timeOutTime = DateTime.Now.AddMilliseconds(timeOut);
            done = false;
            byte[] PbMsg = new byte[msg.BodySize];
            while (DateTime.Now < timeOutTime)
            {
                if (_stream.DataAvailable)
                {
                    _stream.Read(PbMsg, 0, (int)msg.BodySize);
                    done = true;
                    break;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("rec: Stream to short (body) ... waiting");
                    System.Threading.Thread.Sleep(100);
                }
            }
            if (!done)
            {
                System.Diagnostics.Debug.WriteLine("rec: Coudl not get body (" + msg.BodySize + ")");
                return false;
            }

            msg.ProtoBuffMsg = new MemoryStream();
            msg.ProtoBuffMsg.Write(PbMsg, 0, (int)msg.BodySize);
            msg.ProtoBuffMsg.Position = 0;
            return true;
        }

        private byte[] CreatePacketFromMsg(RSProtoBuffSSHMsg msg)
        {
            byte[] a = new byte[4];
            byte[] pbMsg = new byte[msg.BodySize];
            byte[] output = new byte[16 + msg.BodySize];

            a = UintToByteNetwortOrder(msg.MagicCode);
            Array.Copy(a, 0, output, 0, 4);
            a = UintToByteNetwortOrder(msg.MsgID);
            Array.Copy(a, 0, output, 4, 4);
            a = UintToByteNetwortOrder(msg.ReqID);
            Array.Copy(a, 0, output, 8, 4);
            a = UintToByteNetwortOrder(msg.BodySize);
            Array.Copy(a, 0, output, 12, 4);

            msg.ProtoBuffMsg.Read(pbMsg, 0, (int)msg.BodySize);
            msg.ProtoBuffMsg.Position = 0;
            Array.Copy(pbMsg, 0, output, 16, (int)msg.BodySize);

            return output;
        }

        private byte[] UintToByteNetwortOrder(uint i)
        {
            byte[] a = new byte[4];
            a = BitConverter.GetBytes(i);
            Array.Reverse(a);
            return a;
        }

        public uint GetReqID()
        {
            return _nextReqID++;
        }

        // main loop
        private void mainLoop()
        {
            bool foundWork;
            while (_run)
            {
                foundWork = false;
                if (_sendQueue.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine("#######################################");
                    System.Diagnostics.Debug.WriteLine("loop: sending req");
                    RSProtoBuffSSHMsg msg = new RSProtoBuffSSHMsg();
                    lock (_sendQueue)
                        msg = _sendQueue.Dequeue();
                    Send(msg);
                    foundWork = true;
                }

                if (_stream.DataAvailable)
                {
                    System.Diagnostics.Debug.WriteLine("#######################################");
                    System.Diagnostics.Debug.WriteLine("loop: receiving");
                    RSProtoBuffSSHMsg newMsg = new RSProtoBuffSSHMsg();
                    if (Receive(out newMsg))
                        _parent.ProcessMsg(newMsg);
                    foundWork = true;
                }
                
                if(!foundWork)
                    System.Threading.Thread.Sleep(250);
            }
        }

        public void Stop() {         
            _run = false;
        }

        // MsgID functions
        // Lower 8 bits.
        public static byte GetRpcMsgIdSubMsg(uint msgID)
        {
            return BitConverter.GetBytes(msgID)[0];
        }

        // Middle 16 bits.
        public static ushort GetRpcMsgIdService(uint msgID)
        {
            return BitConverter.ToUInt16(BitConverter.GetBytes(msgID), 1);
        }

        // Top 8 bits.
        public static byte GetRpcMsgIdExtension(uint msgID)
        {
            return (byte)(BitConverter.GetBytes(msgID)[3] & 0xFE);
            //return (msg_id >> 24) & 0xFE; // Bottom Bit is for Request / Response
        }

        public static bool IsRpcMsgIdResponse(uint msgID)
        {
            return ((BitConverter.GetBytes(msgID)[3] & 0x01) > 0 ? true : false);
            //return (msg_id >> 24) & 0x01;
        }

        public static uint ConstructMsgId(byte ext, ushort service, byte submsg, bool isResponse)
        {
            if (isResponse)
                ext |= 0x01; // Set Bottom Bit.
            else
                ext &= 0xFE; // Clear Bottom Bit.
            int msg_id = (ext << 24) + (service << 8) + (submsg);
            return Convert.ToUInt32(msg_id);
        }

        public static string GetHex(uint msgID)
        {
            return Convert.ToString(msgID, 16);
        }

        public static string GetHex(byte msgID)
        {
            return Convert.ToString(msgID, 16);
        }

        public static bool Deserialize<T>(out T msg, Stream body)
        {
            try
            {
                msg = Serializer.Deserialize<T>(body);
                return true;
            }
            catch
            {
                msg = default(T);
                return false;
            }
        }

        // Legacy
        //public uint Send<T>(T protoBufMsg, uint msgID)
        //{
        //    System.Diagnostics.Debug.WriteLine("sending packet MsgID: " + msgID);
        //    RSProtoBuffSSHMsg msg = new RSProtoBuffSSHMsg();
        //    msg.MsgID = msgID;
        //    msg.ReqID = GetReqID();
        //    msg.ProtoBuffMsg = new MemoryStream();

        //    using (Stream serialized = new MemoryStream())
        //    {
        //        Serializer.Serialize<T>(serialized, protoBufMsg);
        //        msg.BodySize = (uint)serialized.Length;
        //        serialized.Position = 0;
        //        serialized.CopyTo(msg.ProtoBuffMsg);
        //    }
        //    msg.ProtoBuffMsg.Position = 0;

        //    System.Diagnostics.Debug.WriteLine("body size: " + msg.BodySize);

        //    if (_streamIn.CanWrite)
        //    //if(_stream.CanWrite)
        //    {
        //        //_pendingCallback.Add(msg.ReqID);
        //        _streamIn.Write(CreatePacketFromMsg(msg), 0, 16 + (int)msg.BodySize);
        //        //_stream.Write(CreatePacketFromMsg(msg), 0, 16 + (int)msg.BodySize);
        //    }
        //    else
        //        System.Diagnostics.Debug.WriteLine("kann nicht schreiben");

        //    //_streamIn.Flush();
        //    return msg.ReqID;
        //}
        
        //public T Receive<T>(out uint msgID, uint timeOut = 0)
        //{
        //    msgID = 0;
        //    //if (_pendingCallback.Count == 0)
        //    //    return default(T);

        //    RSProtoBuffSSHMsg msg = new RSProtoBuffSSHMsg();
        //    if (!ReadMsgFromStream(ref msg, timeOut))
        //    {
        //        System.Diagnostics.Debug.WriteLine("Error receiving data from stream");
        //        return default(T);
        //    }

        //    //if (_pendingCallback.Contains(msg.ReqID))
        //    //    _pendingCallback.Remove(msg.ReqID);
        //    //else
        //    //    System.Diagnostics.Debug.WriteLine("rec: unbekannte ReqID");

        //    System.Diagnostics.Debug.WriteLine("rec: pbMsg Length: " + msg.ProtoBuffMsg.Length);

        //    T PbMsg;
        //    try
        //    {
        //        PbMsg = Serializer.Deserialize<T>(msg.ProtoBuffMsg);
        //    }
        //    catch (Exception e)
        //    {
        //        System.Diagnostics.Debug.WriteLine("Error decoding PbMsg: " + e.Message);
        //        return default(T);
        //    }
        //    return PbMsg;
        //}
    }
}
