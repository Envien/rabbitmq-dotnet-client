// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 1.1.
//
// The APL v2.0:
//
//---------------------------------------------------------------------------
//   Copyright (c) 2007-2020 VMware, Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       https://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//---------------------------------------------------------------------------
//
// The MPL v1.1:
//
//---------------------------------------------------------------------------
//  The contents of this file are subject to the Mozilla Public License
//  Version 1.1 (the "License"); you may not use this file except in
//  compliance with the License. You may obtain a copy of the License
//  at https://www.mozilla.org/MPL/
//
//  Software distributed under the License is distributed on an "AS IS"
//  basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See
//  the License for the specific language governing rights and
//  limitations under the License.
//
//  The Original Code is RabbitMQ.
//
//  The Initial Developer of the Original Code is Pivotal Software, Inc.
//  Copyright (c) 2007-2020 VMware, Inc.  All rights reserved.
//---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using RabbitMQ.Client.Events;
using RabbitMQ.Client.Impl;

namespace RabbitMQ.Client.Framing.Impl
{
    internal sealed class AutorecoveringConnection : IConnection
    {
        private readonly object _eventLock = new object();

        private readonly object _manuallyClosedLock = new object();
        private Connection _delegate;
        private ConnectionFactory _factory;

        // list of endpoints provided on initial connection.
        // on re-connection, the next host in the line is chosen using
        // IHostnameSelector
        private IEndpointResolver _endpoints;

        private readonly object _recordedEntitiesLock = new object();

        private List<AutorecoveringModel> _models = new List<AutorecoveringModel>();

        private ConcurrentDictionary<RecordedBinding, byte> _recordedBindings =
            new ConcurrentDictionary<RecordedBinding, byte>();

        private EventHandler<ConnectionBlockedEventArgs> _recordedBlockedEventHandlers;

        private IDictionary<string, RecordedConsumer> _recordedConsumers =
            new ConcurrentDictionary<string, RecordedConsumer>();

        private IDictionary<string, RecordedExchange> _recordedExchanges =
            new ConcurrentDictionary<string, RecordedExchange>();

        private IDictionary<string, RecordedQueue> _recordedQueues =
            new ConcurrentDictionary<string, RecordedQueue>();

        private EventHandler<ShutdownEventArgs> _recordedShutdownEventHandlers;
        private EventHandler<EventArgs> _recordedUnblockedEventHandlers;
        private EventHandler<ConsumerTagChangedAfterRecoveryEventArgs> _consumerTagChange;
        private EventHandler<QueueNameChangedAfterRecoveryEventArgs> _queueNameChange;
        private EventHandler<EventArgs> _recovery;
        private EventHandler<ConnectionRecoveryErrorEventArgs> _connectionRecoveryError;

        public AutorecoveringConnection(ConnectionFactory factory, string clientProvidedName = null)
        {
            _factory = factory;
            ClientProvidedName = clientProvidedName;
        }

        public event EventHandler<EventArgs> RecoverySucceeded
        {
            add
            {
                lock (_eventLock)
                {
                    _recovery += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _recovery -= value;
                }
            }
        }

        public event EventHandler<ConnectionRecoveryErrorEventArgs> ConnectionRecoveryError
        {
            add
            {
                lock (_eventLock)
                {
                    _connectionRecoveryError += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _connectionRecoveryError -= value;
                }
            }
        }

        public event EventHandler<CallbackExceptionEventArgs> CallbackException
        {
            add
            {
                lock (_eventLock)
                {
                    _delegate.CallbackException += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _delegate.CallbackException -= value;
                }
            }
        }

        public event EventHandler<ConnectionBlockedEventArgs> ConnectionBlocked
        {
            add
            {
                lock (_eventLock)
                {
                    _recordedBlockedEventHandlers += value;
                    _delegate.ConnectionBlocked += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _recordedBlockedEventHandlers -= value;
                    _delegate.ConnectionBlocked -= value;
                }
            }
        }

        public event EventHandler<ShutdownEventArgs> ConnectionShutdown
        {
            add
            {
                lock (_eventLock)
                {
                    _recordedShutdownEventHandlers += value;
                    _delegate.ConnectionShutdown += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _recordedShutdownEventHandlers -= value;
                    _delegate.ConnectionShutdown -= value;
                }
            }
        }

        public event EventHandler<EventArgs> ConnectionUnblocked
        {
            add
            {
                lock (_eventLock)
                {
                    _recordedUnblockedEventHandlers += value;
                    _delegate.ConnectionUnblocked += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _recordedUnblockedEventHandlers -= value;
                    _delegate.ConnectionUnblocked -= value;
                }
            }
        }

        public event EventHandler<ConsumerTagChangedAfterRecoveryEventArgs> ConsumerTagChangeAfterRecovery
        {
            add
            {
                lock (_eventLock)
                {
                    _consumerTagChange += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _consumerTagChange -= value;
                }
            }
        }

        public event EventHandler<QueueNameChangedAfterRecoveryEventArgs> QueueNameChangeAfterRecovery
        {
            add
            {
                lock (_eventLock)
                {
                    _queueNameChange += value;
                }
            }
            remove
            {
                lock (_eventLock)
                {
                    _queueNameChange -= value;
                }
            }
        }

        public string ClientProvidedName { get; private set; }

        public ushort ChannelMax
        {
            get { return _delegate.ChannelMax; }
        }

        public ConsumerWorkService ConsumerWorkService
        {
            get { return _delegate.ConsumerWorkService; }
        }

        public IDictionary<string, object> ClientProperties
        {
            get { return _delegate.ClientProperties; }
        }

        public ShutdownEventArgs CloseReason
        {
            get { return _delegate.CloseReason; }
        }

        public AmqpTcpEndpoint Endpoint
        {
            get { return _delegate.Endpoint; }
        }

        public uint FrameMax
        {
            get { return _delegate.FrameMax; }
        }

        public TimeSpan Heartbeat
        {
            get { return _delegate.Heartbeat; }
        }

        public bool IsOpen
        {
            get { return _delegate.IsOpen; }
        }

        public AmqpTcpEndpoint[] KnownHosts
        {
            get { return _delegate.KnownHosts; }
            set { _delegate.KnownHosts = value; }
        }

        public int LocalPort
        {
            get { return _delegate.LocalPort; }
        }

        public ProtocolBase Protocol
        {
            get { return _delegate.Protocol; }
        }

        public IDictionary<string, RecordedExchange> RecordedExchanges
        {
            get { return _recordedExchanges; }
        }

        public IDictionary<string, RecordedQueue> RecordedQueues
        {
            get { return _recordedQueues; }
        }

        public int RemotePort
        {
            get { return _delegate.RemotePort; }
        }

        public IDictionary<string, object> ServerProperties
        {
            get { return _delegate.ServerProperties; }
        }

        public IList<ShutdownReportEntry> ShutdownReport
        {
            get { return _delegate.ShutdownReport; }
        }

        IProtocol IConnection.Protocol
        {
            get { return Endpoint.Protocol; }
        }

        private bool TryPerformAutomaticRecovery()
        {
            ESLog.Info("Performing automatic recovery");

            try
            {
                if (TryRecoverConnectionDelegate())
                {
                    RecoverConnectionShutdownHandlers();
                    RecoverConnectionBlockedHandlers();
                    RecoverConnectionUnblockedHandlers();

                    RecoverModels();
                    if (_factory.TopologyRecoveryEnabled)
                    {
                        RecoverEntities();
                        RecoverConsumers();
                    }

                    ESLog.Info("Connection recovery completed");
                    RunRecoveryEventHandlers();

                    return true;
                }
                else
                {
                    ESLog.Warn("Connection delegate was manually closed. Aborted recovery.");
                }
            }
            catch (Exception e)
            {
                ESLog.Error("Exception when recovering connection. Will try again after retry interval.", e);
            }

            return false;
        }

        public void Close(ShutdownEventArgs reason)
        {
            _delegate.Close(reason);
        }

        public RecoveryAwareModel CreateNonRecoveringModel()
        {
            ISession session = _delegate.CreateSession();
            var result = new RecoveryAwareModel(session);
            result._Private_ChannelOpen("");
            return result;
        }

        public void DeleteRecordedBinding(RecordedBinding rb)
        {
            lock (_recordedEntitiesLock)
            {
                ((IDictionary<RecordedBinding, byte>)_recordedBindings).Remove(rb);
            }
        }

        public RecordedConsumer DeleteRecordedConsumer(string consumerTag)
        {
            RecordedConsumer rc = null;
            lock (_recordedEntitiesLock)
            {
                if (_recordedConsumers.ContainsKey(consumerTag))
                {
                    rc = _recordedConsumers[consumerTag];
                    _recordedConsumers.Remove(consumerTag);
                }
            }

            return rc;
        }

        public void DeleteRecordedExchange(string name)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedExchanges.Remove(name);

                // find bindings that need removal, check if some auto-delete exchanges
                // might need the same
                var bs = _recordedBindings.Keys.Where(b => name.Equals(b.Destination));
                foreach (var b in bs)
                {
                    DeleteRecordedBinding(b);
                    MaybeDeleteRecordedAutoDeleteExchange(b.Source);
                }
            }
        }

        public void DeleteRecordedQueue(string name)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedQueues.Remove(name);
                // find bindings that need removal, check if some auto-delete exchanges
                // might need the same
                var bs = _recordedBindings.Keys.Where(b => name.Equals(b.Destination));
                foreach (var b in bs)
                {
                    DeleteRecordedBinding(b);
                    MaybeDeleteRecordedAutoDeleteExchange(b.Source);
                }
            }
        }

        public bool HasMoreConsumersOnQueue(ICollection<RecordedConsumer> consumers, string queue)
        {
            var cs = new List<RecordedConsumer>(consumers);
            return cs.Exists(c => c.Queue.Equals(queue));
        }

        public bool HasMoreDestinationsBoundToExchange(ICollection<RecordedBinding> bindings, string exchange)
        {
            var bs = new List<RecordedBinding>(bindings);
            return bs.Exists(b => b.Source.Equals(exchange));
        }

        public void MaybeDeleteRecordedAutoDeleteExchange(string exchange)
        {
            lock (_recordedEntitiesLock)
            {
                if (!HasMoreDestinationsBoundToExchange(_recordedBindings.Keys, exchange))
                {
                    _recordedExchanges.TryGetValue(exchange, out RecordedExchange rx);
                    // last binding where this exchange is the source is gone,
                    // remove recorded exchange
                    // if it is auto-deleted. See bug 26364.
                    if ((rx != null) && rx.IsAutoDelete)
                    {
                        _recordedExchanges.Remove(exchange);
                    }
                }
            }
        }

        public void MaybeDeleteRecordedAutoDeleteQueue(string queue)
        {
            lock (_recordedEntitiesLock)
            {
                if (!HasMoreConsumersOnQueue(_recordedConsumers.Values, queue))
                {
                    _recordedQueues.TryGetValue(queue, out RecordedQueue rq);
                    // last consumer on this connection is gone, remove recorded queue
                    // if it is auto-deleted. See bug 26364.
                    if ((rq != null) && rq.IsAutoDelete)
                    {
                        _recordedQueues.Remove(queue);
                    }
                }
            }
        }

        public void RecordBinding(RecordedBinding rb)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedBindings.TryAdd(rb, 0);
            }
        }

        public void RecordConsumer(string name, RecordedConsumer c)
        {
            lock (_recordedEntitiesLock)
            {
                if (!_recordedConsumers.ContainsKey(name))
                {
                    _recordedConsumers.Add(name, c);
                }
            }
        }

        public void RecordExchange(string name, RecordedExchange x)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedExchanges[name] = x;
            }
        }

        public void RecordQueue(string name, RecordedQueue q)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedQueues[name] = q;
            }
        }

        public override string ToString()
        {
            return string.Format("AutorecoveringConnection({0},{1},{2})", _delegate.Id, Endpoint, GetHashCode());
        }

        public void UnregisterModel(AutorecoveringModel model)
        {
            lock (_models)
            {
                _models.Remove(model);
            }
        }

        public void Init()
        {
            Init(_factory.EndpointResolverFactory(new List<AmqpTcpEndpoint> { _factory.Endpoint }));
        }

        public void Init(IEndpointResolver endpoints)
        {
            _endpoints = endpoints;
            var fh = endpoints.SelectOne(_factory.CreateFrameHandler);
            Init(fh);
        }

        private void Init(IFrameHandler fh)
        {
            _delegate = new Connection(_factory, false,
                fh, ClientProvidedName);

            _recoveryTask = Task.Run(MainRecoveryLoop);

            EventHandler<ShutdownEventArgs> recoveryListener = (_, args) =>
            {
                if (ShouldTriggerConnectionRecovery(args))
                {
                    if (!_recoveryLoopCommandQueue.TryAdd(RecoveryCommand.BeginAutomaticRecovery))
                    {
                        ESLog.Warn("Failed to notify RecoveryLoop to BeginAutomaticRecovery.");
                    }
                }
            };
            lock (_eventLock)
            {
                ConnectionShutdown += recoveryListener;
                _recordedShutdownEventHandlers += recoveryListener;
            }
        }

        ///<summary>API-side invocation of updating the secret.</summary>
        public void UpdateSecret(string newSecret, string reason)
        {
            EnsureIsOpen();
            _delegate.UpdateSecret(newSecret, reason);
            _factory.Password = newSecret;
        }

        ///<summary>API-side invocation of connection abort.</summary>
        public void Abort()
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
                _delegate.Abort();
        }

        ///<summary>API-side invocation of connection abort.</summary>
        public void Abort(ushort reasonCode, string reasonText)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
                _delegate.Abort(reasonCode, reasonText);
        }

        ///<summary>API-side invocation of connection abort with timeout.</summary>
        public void Abort(TimeSpan timeout)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Abort(timeout);
            }
        }

        ///<summary>API-side invocation of connection abort with timeout.</summary>
        public void Abort(ushort reasonCode, string reasonText, TimeSpan timeout)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
                _delegate.Abort(reasonCode, reasonText, timeout);
        }

        ///<summary>API-side invocation of connection.close.</summary>
        public void Close()
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
                _delegate.Close();
        }

        ///<summary>API-side invocation of connection.close.</summary>
        public void Close(ushort reasonCode, string reasonText)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
                _delegate.Close(reasonCode, reasonText);
        }

        ///<summary>API-side invocation of connection.close with timeout.</summary>
        public void Close(TimeSpan timeout)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close(timeout);
            }
        }

        ///<summary>API-side invocation of connection.close with timeout.</summary>
        public void Close(ushort reasonCode, string reasonText, TimeSpan timeout)
        {
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close(reasonCode, reasonText, timeout);
            }
        }

        public IModel CreateModel()
        {
            EnsureIsOpen();
            AutorecoveringModel m;
            m = new AutorecoveringModel(this,
                CreateNonRecoveringModel());
            lock (_models)
            {
                _models.Add(m);
            }
            return m;
        }

        public void HandleConnectionBlocked(string reason)
        {
            _delegate.HandleConnectionBlocked(reason);
        }

        public void HandleConnectionUnblocked()
        {
            _delegate.HandleConnectionUnblocked();
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                // dispose managed resources
                try
                {
                    Abort();
                }
                catch (Exception)
                {
                    // TODO: log
                }
                finally
                {
                    _models.Clear();
                    _recordedBlockedEventHandlers = null;
                    _recordedShutdownEventHandlers = null;
                    _recordedUnblockedEventHandlers = null;
                }
            }

            // dispose unmanaged resources
        }

        private void EnsureIsOpen()
        {
            _delegate.EnsureIsOpen();
        }

        private void HandleTopologyRecoveryException(TopologyRecoveryException e)
        {
            ESLog.Error("Topology recovery exception", e);
        }

        private void PropagateQueueNameChangeToBindings(string oldName, string newName)
        {
            lock (_recordedBindings)
            {
                var bs = _recordedBindings.Keys.Where(b => b.Destination.Equals(oldName));
                foreach (RecordedBinding b in bs)
                {
                    b.Destination = newName;
                }
            }
        }

        private void PropagateQueueNameChangeToConsumers(string oldName, string newName)
        {
            lock (_recordedBindings)
            {
                IEnumerable<KeyValuePair<string, RecordedConsumer>> cs = _recordedConsumers.
                    Where(pair => pair.Value.Queue.Equals(oldName));
                foreach (KeyValuePair<string, RecordedConsumer> c in cs)
                {
                    c.Value.Queue = newName;
                }
            }
        }

        private void RecoverBindings()
        {
            foreach (var b in _recordedBindings.Keys)
            {
                try
                {
                    b.Recover();
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering binding between {0} and {1}: {2}",
                        b.Source, b.Destination, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private void RecoverConnectionBlockedHandlers()
        {
            lock (_eventLock)
            {
                _delegate.ConnectionBlocked += _recordedBlockedEventHandlers;
            }
        }

        private bool TryRecoverConnectionDelegate()
        {
            try
            {
                var fh = _endpoints.SelectOne(_factory.CreateFrameHandler);
                _delegate = new Connection(_factory, false, fh, ClientProvidedName);
                return true;
            }
            catch (Exception e)
            {
                ESLog.Error("Connection recovery exception.", e);
                // Trigger recovery error events
                var handler = _connectionRecoveryError;
                if (handler != null)
                {
                    var args = new ConnectionRecoveryErrorEventArgs(e);
                    foreach (EventHandler<ConnectionRecoveryErrorEventArgs> h in handler.GetInvocationList())
                    {
                        try
                        {
                            h(this, args);
                        }
                        catch (Exception ex)
                        {
                            var a = new CallbackExceptionEventArgs(ex);
                            a.Detail["context"] = "OnConnectionRecoveryError";
                            _delegate.OnCallbackException(a);
                        }
                    }
                }
            }

            return false;
        }

        private void RecoverConnectionShutdownHandlers()
        {
            _delegate.ConnectionShutdown += _recordedShutdownEventHandlers;
        }

        private void RecoverConnectionUnblockedHandlers()
        {
            _delegate.ConnectionUnblocked += _recordedUnblockedEventHandlers;
        }

        private void RecoverConsumers()
        {
            foreach (KeyValuePair<string, RecordedConsumer> pair in _recordedConsumers)
            {
                string tag = pair.Key;
                RecordedConsumer cons = pair.Value;

                try
                {
                    string newTag = cons.Recover();
                    lock (_recordedConsumers)
                    {
                        // make sure server-generated tags are re-added
                        _recordedConsumers.Remove(tag);
                        _recordedConsumers.Add(newTag, cons);
                    }

                    if (_consumerTagChange != null)
                    {
                        foreach (EventHandler<ConsumerTagChangedAfterRecoveryEventArgs> h in _consumerTagChange.GetInvocationList())
                        {
                            try
                            {
                                var eventArgs = new ConsumerTagChangedAfterRecoveryEventArgs(tag, newTag);
                                h(this, eventArgs);
                            }
                            catch (Exception e)
                            {
                                var args = new CallbackExceptionEventArgs(e);
                                args.Detail["context"] = "OnConsumerRecovery";
                                _delegate.OnCallbackException(args);
                            }
                        }
                    }
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering consumer {0} on queue {1}: {2}",
                        tag, cons.Queue, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private void RecoverEntities()
        {
            // The recovery sequence is the following:
            //
            // 1. Recover exchanges
            // 2. Recover queues
            // 3. Recover bindings
            // 4. Recover consumers
            RecoverExchanges();
            RecoverQueues();
            RecoverBindings();
        }

        private void RecoverExchanges()
        {
            foreach (RecordedExchange rx in _recordedExchanges.Values)
            {
                try
                {
                    rx.Recover();
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering exchange {0}: {1}",
                        rx.Name, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private void RecoverModels()
        {
            lock (_models)
            {
                foreach (AutorecoveringModel m in _models)
                {
                    m.AutomaticallyRecover(this, _delegate);
                }
            }
        }

        private void RecoverQueues()
        {
            /*
             * TODO LRB
             * This does not exactly match the Java code. In the Java client, a copy of this data is made,
             * then used for iteration
             */
            lock (_recordedEntitiesLock)
            {
                foreach (KeyValuePair<string, RecordedQueue> pair in _recordedQueues)
                {
                    string oldName = pair.Key;
                    RecordedQueue rq = pair.Value;

                    try
                    {
                        rq.Recover();
                        string newName = rq.Name;

                        if (!oldName.Equals(newName))
                        {
                            // Make sure server-named queues are re-added with
                            // their new names.
                            // We only remove old name after we've updated the bindings and consumers,
                            // plus only for server-named queues, both to make sure we don't lose
                            // anything to recover. MK.
                            PropagateQueueNameChangeToBindings(oldName, newName);
                            PropagateQueueNameChangeToConsumers(oldName, newName);
                            // see rabbitmq/rabbitmq-dotnet-client#43
                            if (rq.IsServerNamed)
                            {
                                DeleteRecordedQueue(oldName);
                            }
                            RecordQueue(newName, rq);

                            if (_queueNameChange != null)
                            {
                                foreach (EventHandler<QueueNameChangedAfterRecoveryEventArgs> h in _queueNameChange.GetInvocationList())
                                {
                                    try
                                    {
                                        var eventArgs = new QueueNameChangedAfterRecoveryEventArgs(oldName, newName);
                                        h(this, eventArgs);
                                    }
                                    catch (Exception e)
                                    {
                                        var args = new CallbackExceptionEventArgs(e);
                                        args.Detail["context"] = "OnQueueRecovery";
                                        _delegate.OnCallbackException(args);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception cause)
                    {
                        string s = string.Format("Caught an exception while recovering queue {0}: {1}",
                            oldName, cause.Message);
                        HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                    }
                }
            }
        }

        private void RunRecoveryEventHandlers()
        {
            EventHandler<EventArgs> handler = _recovery;
            if (handler != null)
            {
                foreach (EventHandler<EventArgs> reh in handler.GetInvocationList())
                {
                    try
                    {
                        reh(this, EventArgs.Empty);
                    }
                    catch (Exception e)
                    {
                        var args = new CallbackExceptionEventArgs(e);
                        args.Detail["context"] = "OnConnectionRecovery";
                        _delegate.OnCallbackException(args);
                    }
                }
            }
        }

        private bool ShouldTriggerConnectionRecovery(ShutdownEventArgs args)
        {
            return (args.Initiator == ShutdownInitiator.Peer ||
                    // happens when EOF is reached, e.g. due to RabbitMQ node
                    // connectivity loss or abrupt shutdown
                    args.Initiator == ShutdownInitiator.Library);
        }

        private enum RecoveryCommand
        {
            /// <summary>
            /// Transition to auto-recovery state if not already in that state.
            /// </summary>
            BeginAutomaticRecovery,
            /// <summary>
            /// Attempt to recover connection. If connection is recovered, return
            /// to connected state.
            /// </summary>
            PerformAutomaticRecovery
        }


        private enum RecoveryConnectionState
        {
            /// <summary>
            /// Underlying connection is open.
            /// </summary>
            Connected,
            /// <summary>
            /// In the process of recovering underlying connection.
            /// </summary>
            Recovering
        }

        private Task _recoveryTask;
        private RecoveryConnectionState _recoveryLoopState = RecoveryConnectionState.Connected;

        private readonly BlockingCollection<RecoveryCommand> _recoveryLoopCommandQueue = new BlockingCollection<RecoveryCommand>();
        private readonly CancellationTokenSource _recoveryCancellationToken = new CancellationTokenSource();
        private readonly TaskCompletionSource<int> _recoveryLoopComplete = new TaskCompletionSource<int>();

        /// <summary>
        /// This is the main loop for the auto-recovery thread.
        /// </summary>
        private async Task MainRecoveryLoop()
        {
            try
            {
                while (_recoveryLoopCommandQueue.TryTake(out var command, -1, _recoveryCancellationToken.Token))
                {
                    switch (_recoveryLoopState)
                    {
                        case RecoveryConnectionState.Connected:
                            await RecoveryLoopConnectedHandler(command).ConfigureAwait(false);
                            break;
                        case RecoveryConnectionState.Recovering:
                            await RecoveryLoopRecoveringHandler(command).ConfigureAwait(false);
                            break;
                        default:
                            ESLog.Warn("RecoveryLoop state is out of range.");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected when recovery cancellation token is set.
            }
            catch (Exception e)
            {
                ESLog.Error("Main recovery loop threw unexpected exception.", e);
            }

            _recoveryLoopComplete.SetResult(0);
        }

        /// <summary>
        /// Cancels the main recovery loop and will block until the loop finishes, or the timeout
        /// expires, to prevent Close operations overlapping with recovery operations.
        /// </summary>
        private void StopRecoveryLoop()
        {
            _recoveryCancellationToken.Cancel();
            if (!_recoveryLoopComplete.Task.Wait(_factory.RequestedConnectionTimeout))
            {
                ESLog.Warn("Timeout while trying to stop background AutorecoveringConnection recovery loop.");
            }
        }

        /// <summary>
        /// Handles commands when in the Recovering state.
        /// </summary>
        /// <param name="command"></param>
        private async Task RecoveryLoopRecoveringHandler(RecoveryCommand command)
        {
            switch (command)
            {
                case RecoveryCommand.BeginAutomaticRecovery:
                    ESLog.Info("Received request to BeginAutomaticRecovery, but already in Recovering state.");
                    break;
                case RecoveryCommand.PerformAutomaticRecovery:
                    if (TryPerformAutomaticRecovery())
                    {
                        _recoveryLoopState = RecoveryConnectionState.Connected;
                    }
                    else
                    {
                        await Task.Delay(_factory.NetworkRecoveryInterval);
                        _recoveryLoopCommandQueue.TryAdd(RecoveryCommand.PerformAutomaticRecovery);
                    }

                    break;
                default:
                    ESLog.Warn($"RecoveryLoop command {command} is out of range.");
                    break;
            }
        }

        /// <summary>
        /// Handles commands when in the Connected state.
        /// </summary>
        /// <param name="command"></param>
        private async Task RecoveryLoopConnectedHandler(RecoveryCommand command)
        {
            switch (command)
            {
                case RecoveryCommand.PerformAutomaticRecovery:
                    ESLog.Warn("Not expecting PerformAutomaticRecovery commands while in the connected state.");
                    break;
                case RecoveryCommand.BeginAutomaticRecovery:
                    _recoveryLoopState = RecoveryConnectionState.Recovering;
                    await Task.Delay(_factory.NetworkRecoveryInterval).ConfigureAwait(false);
                    _recoveryLoopCommandQueue.TryAdd(RecoveryCommand.PerformAutomaticRecovery);
                    break;
                default:
                    ESLog.Warn($"RecoveryLoop command {command} is out of range.");
                    break;
            }
        }
    }
}
