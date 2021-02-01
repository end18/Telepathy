﻿// common code used by server and client
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

namespace Telepathy
{
    public abstract class Common
    {
        // common code /////////////////////////////////////////////////////////
        // incoming message queue of <connectionId, message>
        // (not a HashSet because one connection can have multiple new messages)
        protected SafeQueue<Message> receiveQueue = new SafeQueue<Message>();

        // queue count, useful for debugging / benchmarks
        public int ReceiveQueueCount => receiveQueue.Count;

        // disconnect if message queue gets too big.
        // -> avoids ever growing queue memory if network is slower than input
        // -> disconnecting is great for load balancing. better to disconnect
        //    one connection than risking every connection / the whole server
        // -> huge queue would introduce multiple seconds of latency anyway
        //
        // Mirror/DOTSNET use MaxMessageSize batching, so for a 16kb max size:
        //   limit =  1,000 means  16 MB of memory/connection
        //   limit = 10,000 means 160 MB of memory/connection
        public readonly int queueLimit;

        // removes and returns the oldest message from the message queue.
        // (might want to call this until it doesn't return anything anymore)
        // -> Connected, Data, Disconnected events are all added here
        // -> bool return makes while (GetMessage(out Message)) easier!
        // -> no 'is client connected' check because we still want to read the
        //    Disconnected message after a disconnect
        public bool GetNextMessage(out Message message)
        {
            return receiveQueue.TryDequeue(out message);
        }

        // NoDelay disables nagle algorithm. lowers CPU% and latency but
        // increases bandwidth
        public bool NoDelay = true;

        // Prevent allocation attacks. Each packet is prefixed with a length
        // header, so an attacker could send a fake packet with length=2GB,
        // causing the server to allocate 2GB and run out of memory quickly.
        // -> simply increase max packet size if you want to send around bigger
        //    files!
        // -> 16KB per message should be more than enough.
        public int MaxMessageSize = 16 * 1024;

        // Send would stall forever if the network is cut off during a send, so
        // we need a timeout (in milliseconds)
        public int SendTimeout = 5000;

        // avoid header[4] allocations
        readonly byte[] header = new byte[4];

        // avoid payload[packetSize] allocations. size increases dynamically as
        // needed for batching.
        byte[] payload;

        // avoid sendQueue.TryDequeueAll allocations. allocate a list only once.
        // -> we use a List because it automatically grows internally as needed
        // -> won't allocate in hot path except when occasionally growing it
        List<byte[]> dequeueList = new List<byte[]>();

        // constructor /////////////////////////////////////////////////////////
        protected Common(int queueLimit)
        {
            this.queueLimit = queueLimit;
        }

        // helper functions ////////////////////////////////////////////////////
        // send message (via stream) with the <size,content> message structure
        // this function is blocking sometimes!
        // (e.g. if someone has high latency or wire was cut off)
        protected bool SendMessagesBlocking(NetworkStream stream, List<byte[]> messages)
        {
            // stream.Write throws exceptions if client sends with high
            // frequency and the server stops
            try
            {
                // we might have multiple pending messages. merge into one
                // packet to avoid TCP overheads and improve performance.
                //
                // IMPORTANT: Mirror & DOTSNET already batch into MaxMessageSize
                //            chunks, but we STILL pack all pending messages
                //            into one large payload so we only give it to TCP
                //            ONCE. This is HUGE for performance so we keep it!
                int packetSize = 0;
                for (int i = 0; i < messages.Count; ++i)
                    packetSize += 4 + messages[i].Length; // header + content

                // create payload buffer if not created yet or previous one is
                // too small
                // IMPORTANT: payload.Length might be > packetSize! don't use it!
                if (payload == null || payload.Length < packetSize)
                    payload = new byte[packetSize];

                // create the packet
                int position = 0;
                for (int i = 0; i < messages.Count; ++i)
                {
                    // write header (size) into buffer at position
                    Utils.IntToBytesBigEndianNonAlloc(messages[i].Length, payload, position);
                    position += 4;

                    // copy message into buffer
                    Buffer.BlockCopy(messages[i], 0, payload, position, messages[i].Length);
                    position += messages[i].Length;
                }

                // write the whole thing
                stream.Write(payload, 0, packetSize);
                return true;
            }
            catch (Exception exception)
            {
                // log as regular message because servers do shut down sometimes
                Log.Info("Send: stream.Write exception: " + exception);
                return false;
            }
        }

        // read message (via stream) with the <size,content> message structure
        protected bool ReadMessageBlocking(NetworkStream stream, out byte[] content)
        {
            content = null;

            // read exactly 4 bytes for header (blocking)
            if (!stream.ReadExactly(header, 4))
                return false;

            // convert to int
            int size = Utils.BytesToIntBigEndian(header);

            // protect against allocation attacks. an attacker might send
            // multiple fake '2GB header' packets in a row, causing the server
            // to allocate multiple 2GB byte arrays and run out of memory.
            //
            // also protect against size <= 0 which would cause issues
            if (size > 0 && size <= MaxMessageSize)
            {
                // read exactly 'size' bytes for content (blocking)
                // TODO byte[] pool
                content = new byte[size];
                return stream.ReadExactly(content, size);
            }
            Log.Warning("ReadMessageBlocking: possible header attack with a header of: " + size + " bytes.");
            return false;
        }

        // thread receive function is the same for client and server's clients
        protected void ReceiveLoop(int connectionId, TcpClient client)
        {
            // get NetworkStream from client
            NetworkStream stream = client.GetStream();

            // absolutely must wrap with try/catch, otherwise thread exceptions
            // are silent
            try
            {
                // add connected event to queue with ip address as data in case
                // it's needed
                receiveQueue.Enqueue(new Message(connectionId, EventType.Connected, null));

                // let's talk about reading data.
                // -> normally we would read as much as possible and then
                //    extract as many <size,content>,<size,content> messages
                //    as we received this time. this is really complicated
                //    and expensive to do though
                // -> instead we use a trick:
                //      Read(2) -> size
                //        Read(size) -> content
                //      repeat
                //    Read is blocking, but it doesn't matter since the
                //    best thing to do until the full message arrives,
                //    is to wait.
                // => this is the most elegant AND fast solution.
                //    + no resizing
                //    + no extra allocations, just one for the content
                //    + no crazy extraction logic
                while (true)
                {
                    // read the next message (blocking) or stop if stream closed
                    byte[] content;
                    if (!ReadMessageBlocking(stream, out content))
                        // break instead of return so stream close still happens!
                        break;

                    // queue it
                    receiveQueue.Enqueue(new Message(connectionId, EventType.Data, content));

                    // disconnect if receive queue gets too big.
                    // -> avoids ever growing queue memory if network is slower
                    //    than input
                    // -> disconnecting is great for load balancing. better to
                    //    disconnect one connection than risking every
                    //    connection / the whole server
                    if (receiveQueue.Count >= queueLimit)
                    {
                        // log the reason
                        Log.Warning($"receiveQueue reached limit of {queueLimit}. This can happen if network messages come in way faster than we manage to process them. Disconnecting this connection for load balancing.");

                        // clear queue so the final disconnect message will be
                        // processed immediately. no need to process thousands
                        // of pending messages before disconnecting.
                        // it would just delay it for quite some time.
                        receiveQueue.Clear();

                        // just break. the finally{} will close everything.
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                // something went wrong. the thread was interrupted or the
                // connection closed or we closed our own connection or ...
                // -> either way we should stop gracefully
                Log.Info("ReceiveLoop: finished receive function for connectionId=" + connectionId + " reason: " + exception);
            }
            finally
            {
                // clean up no matter what
                stream.Close();
                client.Close();

                // add 'Disconnected' message after disconnecting properly.
                // -> always AFTER closing the streams to avoid a race condition
                //    where Disconnected -> Reconnect wouldn't work because
                //    Connected is still true for a short moment before the stream
                //    would be closed.
                receiveQueue.Enqueue(new Message(connectionId, EventType.Disconnected, null));
            }
        }

        // thread send function
        // note: we really do need one per connection, so that if one connection
        //       blocks, the rest will still continue to get sends
        protected void SendLoop(int connectionId, TcpClient client, SafeQueue<byte[]> sendQueue, ManualResetEvent sendPending)
        {
            // get NetworkStream from client
            NetworkStream stream = client.GetStream();

            try
            {
                while (client.Connected) // try this. client will get closed eventually.
                {
                    // reset ManualResetEvent before we do anything else. this
                    // way there is no race condition. if Send() is called again
                    // while in here then it will be properly detected next time
                    // -> otherwise Send might be called right after dequeue but
                    //    before .Reset, which would completely ignore it until
                    //    the next Send call.
                    sendPending.Reset(); // WaitOne() blocks until .Set() again

                    // dequeue all
                    // SafeQueue.TryDequeueAll is twice as fast as
                    // ConcurrentQueue, see SafeQueue.cs!
                    if (sendQueue.TryDequeueAll(dequeueList))
                    {
                        // send message (blocking) or stop if stream is closed
                        if (!SendMessagesBlocking(stream, dequeueList))
                            // break instead of return so stream close still happens!
                            break;
                    }

                    // clear list for next time
                    dequeueList.Clear();

                    // don't choke up the CPU: wait until queue not empty anymore
                    sendPending.WaitOne();
                }
            }
            catch (ThreadAbortException)
            {
                // happens on stop. don't log anything.
            }
            catch (ThreadInterruptedException)
            {
                // happens if receive thread interrupts send thread.
            }
            catch (Exception exception)
            {
                // something went wrong. the thread was interrupted or the
                // connection closed or we closed our own connection or ...
                // -> either way we should stop gracefully
                Log.Info("SendLoop Exception: connectionId=" + connectionId + " reason: " + exception);
            }
            finally
            {
                // clean up no matter what
                // we might get SocketExceptions when sending if the 'host has
                // failed to respond' - in which case we should close the connection
                // which causes the ReceiveLoop to end and fire the Disconnected
                // message. otherwise the connection would stay alive forever even
                // though we can't send anymore.
                stream.Close();
                client.Close();
            }
        }
    }
}
