﻿using Megumin.Message.TestMessage;
using Megumin.Remote;
using Net.Remote;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Megumin.Message
{
    public partial class MessagePipeline:IMessagePipeline
    {
        #region Message

        /// <summary>
        /// 描述消息包长度字节所占的字节数
        /// <para>长度类型ushort，所以一个包理论最大长度不能超过65535字节，框架要求一个包不能大于8192 - 25 个 字节</para>
        /// 
        /// 按照千兆网卡计算，一个玩家每秒10~30包，大约10~30KB，大约能负载3000玩家。
        /// </summary>
        public const int MessageLengthByteCount = sizeof(ushort);

        /// <summary>
        /// 消息包类型ID 字节长度
        /// </summary>
        public const int MessageIDByteCount = sizeof(int);

        /// <summary>
        /// 消息包类型ID 字节长度
        /// </summary>
        public const int RpcIDByteCount = sizeof(ushort);

        /// <summary>
        /// 报头初始偏移6, rpcID贴合在消息正文，不算报头。
        /// </summary>
        public const int HeaderOffset = 2 + 4;

        #endregion

        /// <summary>
        /// 默认开启线程转换
        /// </summary>
        public bool Post2ThreadScheduler { get; set; } = true;
        public readonly static MessagePipeline Default = new MessagePipeline();



        /// <summary>
        /// 分离粘包
        /// <para> <see cref="Packet(short, object, IRemote)"/> 对应 </para>
        /// </summary>
        /// <param name="source"></param>
        /// <param name="pushCompleteMessage"></param>
        /// <returns>剩余的半包。</returns>
        public virtual ReadOnlySpan<byte> CutOff(ReadOnlySpan<byte> source, IList<IMemoryOwner<byte>> pushCompleteMessage)
        {
            var length = source.Length;
            ///已经完整读取消息包的长度
            int offset = 0;
            ///长度至少要大于2（2个字节表示消息总长度）
            while (length - offset > 2)
            {

                ///取得单个消息总长度
                ushort size = source.Slice(offset).ReadUshort();
                if (length - offset < size)
                {
                    ///剩余消息长度不是一个完整包
                    break;
                }

                /// 使用内存池
                var newMsg = BufferPool.Rent(size);

                source.Slice(offset, size).CopyTo(newMsg.Memory.Span);
                pushCompleteMessage.Add(newMsg);

                offset += size;
            }

            ///返回剩余的半包。
            return source.Slice(offset, length - offset);
        }

        public async void Push<T>(IMemoryOwner<byte> packet, T bufferReceiver)
            where T:ISendMessage,IRemoteID,IUID,IObjectMessageReceiver
        {
            try
            {
                var memory = packet.Memory;

                var (messageID, extraMessage, messageBody) = UnPacket(memory);

                if (PreDeserialize(messageID, extraMessage, messageBody, bufferReceiver))
                {
                    var (rpcID, message) = DeserializeMessage(messageID, messageBody);

                    if (PostDeserialize(messageID, extraMessage, messageBody, bufferReceiver))
                    {
                        if (Post2ThreadScheduler)
                        {
                            var resp = await MessageThreadTransducer.Push(rpcID, message, bufferReceiver);

                            Reply(bufferReceiver, extraMessage, rpcID, resp);
                        }
                        else
                        {
                            var resp = await bufferReceiver.Deal(rpcID, message);

                            if (resp is Task<object> task)
                            {
                                resp = await task;
                            }

                            if (resp is ValueTask<object> vtask)
                            {
                                resp = await vtask;
                            }

                            Reply(bufferReceiver, extraMessage, rpcID, resp);
                        }
                    }
                }
            }
            finally
            {
                packet.Dispose();
            }
        }

        private void Reply<T>(T bufferReceiver, ReadOnlyMemory<byte> extraMessage, int rpcID, object resp) where T : ISendMessage, IRemoteID, IUID, IObjectMessageReceiver
        {
            if (resp != null)
            {
                RoutingInformationModifier routeTableWriter = new RoutingInformationModifier(extraMessage);
                routeTableWriter.ReverseDirection();
                var b = Packet(rpcID * -1, resp, routeTableWriter);
                bufferReceiver.SendAsync(b);
                routeTableWriter.Dispose();
            }
        }

        /// <summary>
        /// 转发
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="remote"></param>
        /// <param name="messageID"></param>
        /// <param name="extraMessage"></param>
        /// <param name="messageBody"></param>
        /// <param name="forwarder"></param>
        public virtual void Forward<T>(T bufferReceiver, int messageID, ReadOnlyMemory<byte> extraMessage, ReadOnlyMemory<byte> messageBody, IForwarder forwarder) 
            where T : IRemoteID,IUID
        {
            RoutingInformationModifier modifier = extraMessage;
            if (modifier.Mode == RouteMode.Null)
            {
                modifier.Identifier = bufferReceiver.UID;
            }
            else if (modifier.Mode == RouteMode.Find)
            {
                modifier = new RoutingInformationModifier(extraMessage);
                modifier.AddNode(bufferReceiver, forwarder);
            }
            else
            {
                modifier = new RoutingInformationModifier(extraMessage);
                modifier.MoveCursorNext();
            }

            forwarder.SendAsync(Packet(messageID, extraMessage: modifier, messageBody.Span));
            modifier.Dispose();
        }
    }

    ///处理路由转发过程
    partial class MessagePipeline
    {
        /// <summary>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="messageID"></param>
        /// <param name="routeTable"></param>
        /// <param name="messageBody"></param>
        /// <param name="bufferReceiver"></param>
        /// <returns></returns>
        public virtual bool PreDeserialize<T>(int messageID,in ReadOnlyMemory<byte> extraMessage,
            in ReadOnlyMemory<byte> messageBody,T bufferReceiver)
            where T:IRemoteID,IUID
        {
            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="messageID"></param>
        /// <param name="routeTable"></param>
        /// <param name="messageBody"></param>
        /// <param name="bufferReceiver"></param>
        /// <returns></returns>
        public virtual bool PostDeserialize<T>(int messageID,in ReadOnlyMemory<byte> extraMessage,
            in ReadOnlyMemory<byte> messageBody,T bufferReceiver)
            where T:IRemoteID
        {
            return true;
        }
    }

    ///打包封包
    partial class MessagePipeline
    {
        /// <summary>
        /// 普通打包
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rpcID"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public virtual IMemoryOwner<byte> Packet(int rpcID, object message)
        {
            ///序列化用buffer,使用内存池
            using (var memoryOwner = BufferPool.Rent(16384))
            {
                Span<byte> span = memoryOwner.Memory.Span;

                var (messageID, length) = SerializeMessage(message, rpcID, span);

                ///省略了额外消息
                var sendbuffer = Packet(messageID, extraMessage:RoutingInformationModifier.Empty, span.Slice(0, length));
                return sendbuffer;
            }
        }

        /// <summary>
        /// 转发打包
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rpcID"></param>
        /// <param name="message"></param>
        /// <param name="identifier"></param>
        /// <returns></returns>
        public virtual IMemoryOwner<byte> Packet(int rpcID, object message, int identifier)
        {
            ///序列化用buffer,使用内存池
            using (var memoryOwner = BufferPool.Rent(16384))
            {
                Span<byte> span = memoryOwner.Memory.Span;

                var (messageID, length) = SerializeMessage(message, rpcID, span);

                var routeTable = new RoutingInformationModifier(identifier);
                var res = Packet(messageID, extraMessage:routeTable, span.Slice(0, length));
                routeTable.Dispose();
                return res;
            }
        }

        /// <summary>
        /// 返回消息打包
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rpcID"></param>
        /// <param name="message"></param>
        /// <param name="extraMessage"></param>
        /// <returns></returns>
        public virtual IMemoryOwner<byte> Packet(int rpcID, object message, ReadOnlySpan<byte> extraMessage)
        {
            ///序列化用buffer,使用内存池
            using (var memoryOwner = BufferPool.Rent(16384))
            {
                Span<byte> span = memoryOwner.Memory.Span;

                var (messageID, length) = SerializeMessage(message, rpcID, span);

                return Packet(messageID, extraMessage, span.Slice(0, length));
            }
        }

        /// <summary>
        /// 封装将要发送的字节消息,这个方法控制消息字节的布局
        /// <para>框架使用的字节布局 2总长度 + 4消息ID +  extraMessage + messageBody</para>
        /// </summary>
        /// <param name="messageID"></param>
        /// <param name="extraMessage"></param>
        /// <param name="messageBody"></param>
        /// <returns>框架使用BigEndian</returns>
        public virtual IMemoryOwner<byte> Packet(int messageID, ReadOnlySpan<byte> extraMessage, ReadOnlySpan<byte> messageBody)
        {
            if (extraMessage.IsEmpty)
            {
                throw new ArgumentNullException($"额外消息部分至少长度为1");
            }
            ushort totolLength = (ushort)(HeaderOffset + extraMessage.Length + messageBody.Length);

            ///申请发送用 buffer ((框架约定1)发送字节数组发送完成后由发送逻辑回收)         额外信息的最大长度17
            var sendbufferOwner = BufferPool.Rent(totolLength);
            var span = sendbufferOwner.Memory.Span;

            ///写入报头 大端字节序写入
            totolLength.WriteTo(span);
            messageID.WriteTo(span.Slice(2));


            ///拷贝额外消息
            extraMessage.CopyTo(span.Slice(HeaderOffset));
            ///拷贝消息正文
            messageBody.CopyTo(span.Slice(HeaderOffset + extraMessage.Length));

            return sendbufferOwner;
        }

        /// <summary>
        /// 解析报头 (长度至少要大于6（6个字节也就是一个报头长度）)
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException">数据长度小于报头长度</exception>
        public virtual (ushort totalLenght, int messageID)
            ParsePacketHeader(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length >= HeaderOffset)
            {
                ushort size = buffer.ReadUshort();

                int messageID = buffer.Slice(2).ReadInt();

                return (size, messageID);
            }
            else
            {
                throw new ArgumentOutOfRangeException("数据长度小于报头长度");
            }
        }

        /// <summary>
        /// 解包。 这个方法解析消息字节的布局
        /// <para> 和 <see cref="Packet(int, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> 对应</para>
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        /// <remarks>分离消息是使用报头描述的长度而不能依赖于Span长度</remarks>
        public virtual (int messageID, ReadOnlyMemory<byte> extraMessage, ReadOnlyMemory<byte> messageBody)
            UnPacket(ReadOnlyMemory<byte> buffer)
        {
            ReadOnlySpan<byte> span = buffer.Span;
            var (totalLenght, messageID) = ParsePacketHeader(span);
            var extralength = span.Slice(HeaderOffset)[0];

            var extraMessage = buffer.Slice(HeaderOffset, extralength);

            int start = HeaderOffset + extralength;
            ///分离消息是使用报头描述的长度而不能依赖于Span长度
            var messageBody = buffer.Slice(start, totalLenght - start);
            return (messageID, extraMessage, messageBody);
        }
    }

    ///消息正文处理
    partial class MessagePipeline:IDeserializeHandle
    {
        /// <summary>
        /// 序列化消息阶段
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="span"></param>
        /// <param name="message"></param>
        /// <param name="rpcID"></param>
        /// <returns></returns>
        public virtual (int messageID, ushort length)
            SerializeMessage(object message, int rpcID, Span<byte> span)
        {
            ///rpcID直接附加值消息正文前4位。
            rpcID.WriteTo(span);
            var (messageID, length) = MessageLUT.Serialize(message, span.Slice(4));
            return (messageID, (ushort)(length + 4));
        }

        /// <summary>
        /// 反序列化消息阶段
        /// </summary>
        /// <returns></returns>
        public virtual (int rpcID,object message) DeserializeMessage(int messageID,in ReadOnlyMemory<byte> messageBody)
        {
            var rpcID = messageBody.Span.ReadInt();
            var message = MessageLUT.Deserialize(messageID, messageBody.Slice(4));
            return (rpcID, message);
        }
    }

    internal class GateServerMessagePipeline:MessagePipeline
    {

        public override bool PreDeserialize<T>(int messageID,in ReadOnlyMemory<byte> extraMessage,in ReadOnlyMemory<byte> messageBody, T bufferReceiver)
        {
            RoutingInformationModifier information = extraMessage;
            if (information.Mode == RouteMode.Backward || information.Mode == RouteMode.Forward)
            {
                var forwarder = GetForward(information.Next);
                if (forwarder != null)
                {
                    Forward(bufferReceiver, messageID, extraMessage, messageBody, forwarder);
                    return false;
                }
            }
            return true;
        }

        private IForwarder GetForward(int? next)
        {
            throw new NotImplementedException();
        }
    }

    internal class BattleServerMP:MessagePipeline
    {
        
    }



    public class TestFunction
    {
        static int totalCount = 0;
        public static async ValueTask<object> DealMessage(object message,IReceiveMessage receiver)
        {
            totalCount++;
            switch (message)
            {
                case TestPacket1 packet1:
                    if (totalCount % 100 == 0)
                    {
                        Console.WriteLine($"接收消息{nameof(TestPacket1)}--{packet1.Value}------总消息数{totalCount}"); 
                    }
                    return null;
                case TestPacket2 packet2:
                    Console.WriteLine($"接收消息{nameof(TestPacket2)}--{packet2.Value}");
                    return new TestPacket2 { Value = packet2.Value };
                default:
                    break;
            }
            return null;
        }
    }
}
