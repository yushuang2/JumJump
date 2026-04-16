using System;
using System.IO;
using Google.Protobuf;
using JumJump.Proto;

namespace JumJump.Network
{
    public static class ProtocolCodec
    {
        public static byte[] Pack<T>(Cmd cmd, ulong seq, string roomId, uint roomVersion, T message) where T : IMessage
        {
            var payload = message.ToByteString();
            var env = new Envelope
            {
                Cmd = (uint)cmd,
                Seq = seq,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = payload,
                RoomId = roomId ?? string.Empty,
                RoomVersion = roomVersion,
            };

            using var ms = new MemoryStream();
            env.WriteTo(ms);
            return ms.ToArray();
        }

        public static Envelope Unpack(byte[] data)
        {
            return Envelope.Parser.ParseFrom(data);
        }

        public static T ParsePayload<T>(Envelope env, MessageParser<T> parser) where T : IMessage<T>
        {
            return parser.ParseFrom(env.Payload);
        }
    }
}
