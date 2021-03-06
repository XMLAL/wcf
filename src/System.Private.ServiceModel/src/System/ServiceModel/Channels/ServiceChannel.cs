// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.Diagnostics;
using System.ServiceModel.Description;
using System.ServiceModel.Diagnostics;
using System.ServiceModel.Diagnostics.Application;
using System.ServiceModel.Dispatcher;
using System.ServiceModel.Security;
using System.Threading;
using System.Threading.Tasks;

namespace System.ServiceModel.Channels
{
    // This class is sealed because the constructor could call Abort, which is virtual
    internal sealed class ServiceChannel : CommunicationObject, IChannel, IClientChannel, IDuplexContextChannel, IOutputChannel, IRequestChannel, IServiceChannel
    {
        private int _activityCount = 0;
        private bool _allowInitializationUI = true;
        private bool _allowOutputBatching = false;
        private bool _autoClose = true;
        private CallOnceManager _autoDisplayUIManager;
        private CallOnceManager _autoOpenManager;
        private readonly IChannelBinder _binder;
        private readonly ChannelDispatcher _channelDispatcher;
        private ClientRuntime _clientRuntime;
        private readonly bool _closeBinder = true;
        private bool _closeFactory;
        private bool _didInteractiveInitialization;
        private bool _doneReceiving;
        private EndpointDispatcher _endpointDispatcher;
        private bool _explicitlyOpened;
        private ExtensionCollection<IContextChannel> _extensions;
        private readonly ServiceChannelFactory _factory;
        private readonly bool _hasSession;
        private readonly SessionIdleManager _idleManager;
        private InstanceContext _instanceContext;
        private bool _isPending;
        private readonly bool _isReplyChannel;
        private EndpointAddress _localAddress;
        private readonly MessageVersion _messageVersion;
        private readonly bool _openBinder = false;
        private TimeSpan _operationTimeout;
        private object _proxy;
        private string _terminatingOperationName;
        private bool _hasChannelStartedAutoClosing;
        private bool _hasCleanedUpChannelCollections;
        private EventTraceActivity _eventActivity;

        private EventHandler<UnknownMessageReceivedEventArgs> _unknownMessageReceived;

        private ServiceChannel(IChannelBinder binder, MessageVersion messageVersion, IDefaultCommunicationTimeouts timeouts)
        {
            if (binder == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgumentNull("binder");
            }

            _messageVersion = messageVersion;
            _binder = binder;
            _isReplyChannel = _binder.Channel is IReplyChannel;

            IChannel innerChannel = binder.Channel;
            _hasSession = (innerChannel is ISessionChannel<IDuplexSession>) ||
                        (innerChannel is ISessionChannel<IInputSession>) ||
                        (innerChannel is ISessionChannel<IOutputSession>);

            this.IncrementActivity();
            _openBinder = (binder.Channel.State == CommunicationState.Created);

            _operationTimeout = timeouts.SendTimeout;
        }

        internal ServiceChannel(ServiceChannelFactory factory, IChannelBinder binder)
            : this(binder, factory.MessageVersion, factory)
        {
            _factory = factory;
            _clientRuntime = factory.ClientRuntime;

            this.SetupInnerChannelFaultHandler();

            DispatchRuntime dispatch = factory.ClientRuntime.DispatchRuntime;
            if (dispatch != null)
            {
                _autoClose = dispatch.AutomaticInputSessionShutdown;
            }

            factory.ChannelCreated(this);
        }

        internal ServiceChannel(IChannelBinder binder,
                                EndpointDispatcher endpointDispatcher,
                                ChannelDispatcher channelDispatcher,
                                SessionIdleManager idleManager)
            : this(binder, channelDispatcher.MessageVersion, channelDispatcher.DefaultCommunicationTimeouts)
        {
            if (endpointDispatcher == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgumentNull("endpointDispatcher");
            }

            _channelDispatcher = channelDispatcher;
            _endpointDispatcher = endpointDispatcher;
            _clientRuntime = endpointDispatcher.DispatchRuntime.CallbackClientRuntime;

            this.SetupInnerChannelFaultHandler();

            _autoClose = endpointDispatcher.DispatchRuntime.AutomaticInputSessionShutdown;
            _isPending = true;

            IDefaultCommunicationTimeouts timeouts = channelDispatcher.DefaultCommunicationTimeouts;
            _idleManager = idleManager;

            if (!binder.HasSession)
                _closeBinder = false;

            if (_idleManager != null)
            {
                bool didIdleAbort;
                _idleManager.RegisterChannel(this, out didIdleAbort);
                if (didIdleAbort)
                {
                    this.Abort();
                }
            }
        }

        private CallOnceManager AutoOpenManager
        {
            get
            {
                if (!_explicitlyOpened && (_autoOpenManager == null))
                {
                    this.EnsureAutoOpenManagers();
                }
                return _autoOpenManager;
            }
        }

        private CallOnceManager AutoDisplayUIManager
        {
            get
            {
                if (!_explicitlyOpened && (_autoDisplayUIManager == null))
                {
                    this.EnsureAutoOpenManagers();
                }
                return _autoDisplayUIManager;
            }
        }


        internal EventTraceActivity EventActivity
        {
            get
            {
                if (_eventActivity == null)
                {
                    //Take the id on the thread so that we know the initiating operation.
                    _eventActivity = EventTraceActivity.GetFromThreadOrCreate();
                }
                return _eventActivity;
            }
        }

        internal bool CloseFactory
        {
            get { return _closeFactory; }
            set { _closeFactory = value; }
        }

        protected override TimeSpan DefaultCloseTimeout
        {
            get { return this.CloseTimeout; }
        }

        protected override TimeSpan DefaultOpenTimeout
        {
            get { return this.OpenTimeout; }
        }

        internal DispatchRuntime DispatchRuntime
        {
            get
            {
                if (_endpointDispatcher != null)
                {
                    return _endpointDispatcher.DispatchRuntime;
                }
                if (_clientRuntime != null)
                {
                    return _clientRuntime.DispatchRuntime;
                }
                return null;
            }
        }

        internal MessageVersion MessageVersion
        {
            get { return _messageVersion; }
        }

        internal IChannelBinder Binder
        {
            get { return _binder; }
        }

        internal TimeSpan CloseTimeout
        {
            get
            {
                if (this.IsClient)
                {
                    return _factory.InternalCloseTimeout;
                }
                else
                {
                    return this.ChannelDispatcher.InternalCloseTimeout;
                }
            }
        }

        internal ChannelDispatcher ChannelDispatcher
        {
            get { return _channelDispatcher; }
        }

        internal EndpointDispatcher EndpointDispatcher
        {
            get { return _endpointDispatcher; }
            set
            {
                lock (this.ThisLock)
                {
                    _endpointDispatcher = value;
                    _clientRuntime = value.DispatchRuntime.CallbackClientRuntime;
                }
            }
        }

        internal ServiceChannelFactory Factory
        {
            get { return _factory; }
        }

        internal IChannel InnerChannel
        {
            get { return _binder.Channel; }
        }

        internal bool IsPending
        {
            get { return _isPending; }
            set { _isPending = value; }
        }

        internal bool HasSession
        {
            get { return _hasSession; }
        }

        internal bool IsClient
        {
            get { return _factory != null; }
        }

        internal bool IsReplyChannel
        {
            get { return _isReplyChannel; }
        }

        public Uri ListenUri
        {
            get
            {
                return _binder.ListenUri;
            }
        }

        public EndpointAddress LocalAddress
        {
            get
            {
                if (_localAddress == null)
                {
                    if (_endpointDispatcher != null)
                    {
                        _localAddress = _endpointDispatcher.EndpointAddress;
                    }
                    else
                    {
                        _localAddress = _binder.LocalAddress;
                    }
                }
                return _localAddress;
            }
        }

        internal TimeSpan OpenTimeout
        {
            get
            {
                if (this.IsClient)
                {
                    return _factory.InternalOpenTimeout;
                }
                else
                {
                    return this.ChannelDispatcher.InternalOpenTimeout;
                }
            }
        }

        public TimeSpan OperationTimeout
        {
            get { return _operationTimeout; }
            set
            {
                if (value < TimeSpan.Zero)
                {
                    string message = SR.SFxTimeoutOutOfRange0;
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ArgumentOutOfRangeException("value", value, message));
                }
                if (TimeoutHelper.IsTooLarge(value))
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ArgumentOutOfRangeException("value", value, SR.SFxTimeoutOutOfRangeTooBig));
                }


                _operationTimeout = value;
            }
        }

        internal object Proxy
        {
            get
            {
                object proxy = _proxy;
                if (proxy != null)
                    return proxy;
                else
                    return this;
            }
            set
            {
                _proxy = value;
                base.EventSender = value;   // need to use "proxy" as open/close event source
            }
        }

        internal ClientRuntime ClientRuntime
        {
            get { return _clientRuntime; }
        }

        public EndpointAddress RemoteAddress
        {
            get
            {
                IOutputChannel outputChannel = this.InnerChannel as IOutputChannel;
                if (outputChannel != null)
                    return outputChannel.RemoteAddress;

                IRequestChannel requestChannel = this.InnerChannel as IRequestChannel;
                if (requestChannel != null)
                    return requestChannel.RemoteAddress;

                return null;
            }
        }

        private ProxyOperationRuntime UnhandledProxyOperation
        {
            get { return this.ClientRuntime.GetRuntime().UnhandledProxyOperation; }
        }

        public Uri Via
        {
            get
            {
                IOutputChannel outputChannel = this.InnerChannel as IOutputChannel;
                if (outputChannel != null)
                    return outputChannel.Via;

                IRequestChannel requestChannel = this.InnerChannel as IRequestChannel;
                if (requestChannel != null)
                    return requestChannel.Via;

                return null;
            }
        }

        internal InstanceContext InstanceContext
        {
            get { return _instanceContext; }
            set { _instanceContext = value; }
        }

        private void SetupInnerChannelFaultHandler()
        {
            // need to call this method after this.binder and this.clientRuntime are set to prevent a potential 
            // NullReferenceException in this method or in the OnInnerChannelFaulted method; 
            // because this method accesses this.binder and OnInnerChannelFaulted accesses this.clientRuntime.
            _binder.Channel.Faulted += OnInnerChannelFaulted;
        }

        private void BindDuplexCallbacks()
        {
            IDuplexChannel duplexChannel = this.InnerChannel as IDuplexChannel;
            if ((duplexChannel != null) && (_factory != null) && (_instanceContext != null))
            {
                if (_binder is DuplexChannelBinder)
                    ((DuplexChannelBinder)_binder).EnsurePumping();
            }
        }

        internal bool CanCastTo(Type t)
        {
            if (t.IsAssignableFrom(typeof(IClientChannel)))
                return true;

            if (t.IsAssignableFrom(typeof(IDuplexContextChannel)))
                return this.InnerChannel is IDuplexChannel;

            if (t.IsAssignableFrom(typeof(IServiceChannel)))
                return true;

            return false;
        }

        internal void CompletedIOOperation()
        {
            if (_idleManager != null)
            {
                _idleManager.CompletedActivity();
            }
        }

        private void EnsureAutoOpenManagers()
        {
            lock (this.ThisLock)
            {
                if (!_explicitlyOpened)
                {
                    if (_autoOpenManager == null)
                    {
                        _autoOpenManager = new CallOnceManager(this, CallOpenOnce.Instance);
                    }
                    if (_autoDisplayUIManager == null)
                    {
                        _autoDisplayUIManager = new CallOnceManager(this, CallDisplayUIOnce.Instance);
                    }
                }
            }
        }

        private void EnsureDisplayUI()
        {
            CallOnceManager manager = this.AutoDisplayUIManager;
            if (manager != null)
            {
                manager.CallOnce(TimeSpan.MaxValue, null);
            }
            this.ThrowIfInitializationUINotCalled();
        }

        private IAsyncResult BeginEnsureDisplayUI(AsyncCallback callback, object state)
        {
            CallOnceManager manager = this.AutoDisplayUIManager;
            if (manager != null)
            {
                return manager.BeginCallOnce(TimeSpan.MaxValue, null, callback, state);
            }
            else
            {
                return new CallOnceCompletedAsyncResult(callback, state);
            }
        }

        private void EndEnsureDisplayUI(IAsyncResult result)
        {
            CallOnceManager manager = this.AutoDisplayUIManager;
            if (manager != null)
            {
                manager.EndCallOnce(result);
            }
            else
            {
                CallOnceCompletedAsyncResult.End(result);
            }
            this.ThrowIfInitializationUINotCalled();
        }

        private void EnsureOpened(TimeSpan timeout)
        {
            CallOnceManager manager = this.AutoOpenManager;
            if (manager != null)
            {
                manager.CallOnce(timeout, _autoDisplayUIManager);
            }

            this.ThrowIfOpening();
            this.ThrowIfDisposedOrNotOpen();
        }

        private IAsyncResult BeginEnsureOpened(TimeSpan timeout, AsyncCallback callback, object state)
        {
            CallOnceManager manager = this.AutoOpenManager;
            if (manager != null)
            {
                return manager.BeginCallOnce(timeout, _autoDisplayUIManager, callback, state);
            }
            else
            {
                this.ThrowIfOpening();
                this.ThrowIfDisposedOrNotOpen();

                return new CallOnceCompletedAsyncResult(callback, state);
            }
        }

        private void EndEnsureOpened(IAsyncResult result)
        {
            CallOnceManager manager = this.AutoOpenManager;
            if (manager != null)
            {
                manager.EndCallOnce(result);
            }
            else
            {
                CallOnceCompletedAsyncResult.End(result);
            }
        }

        public T GetProperty<T>() where T : class
        {
            IChannel innerChannel = this.InnerChannel;
            if (innerChannel != null)
                return innerChannel.GetProperty<T>();
            return null;
        }

        private void PrepareCall(ProxyOperationRuntime operation, bool oneway, ref ProxyRpc rpc)
        {
            OperationContext context = OperationContext.Current;
            // Doing a request reply callback when dispatching in-order deadlocks.
            // We never receive the reply until we finish processing the current message.
            if (!oneway)
            {
                DispatchRuntime dispatchBehavior = this.ClientRuntime.DispatchRuntime;
                if ((dispatchBehavior != null) && (dispatchBehavior.ConcurrencyMode == ConcurrencyMode.Single))
                {
                    if ((context != null) && (!context.IsUserContext) && (context.InternalServiceChannel == this))
                    {
                        throw ExceptionHelper.PlatformNotSupported();
                        //throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.Format(SR.SFxCallbackRequestReplyInOrder1, typeof(CallbackBehaviorAttribute).Name)));
                    }
                }
            }

            if ((this.State == CommunicationState.Created) && !operation.IsInitiating)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.Format(SR.SFxNonInitiatingOperation1, operation.Name)));
            }

            if (_terminatingOperationName != null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.Format(SR.SFxTerminatingOperationAlreadyCalled1, _terminatingOperationName)));
            }

            if (_hasChannelStartedAutoClosing)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ProtocolException(SR.SFxClientOutputSessionAutoClosed));
            }

            operation.BeforeRequest(ref rpc);
            AddMessageProperties(rpc.Request, context);
            if (!oneway && !this.ClientRuntime.ManualAddressing && rpc.Request.Version.Addressing != AddressingVersion.None)
            {
                RequestReplyCorrelator.PrepareRequest(rpc.Request);

                MessageHeaders headers = rpc.Request.Headers;
                EndpointAddress localAddress = this.LocalAddress;
                EndpointAddress replyTo = headers.ReplyTo;

                if (replyTo == null)
                {
                    headers.ReplyTo = localAddress ?? EndpointAddress.AnonymousAddress;
                }

                if (this.IsClient && (localAddress != null) && !localAddress.IsAnonymous)
                {
                    Uri localUri = localAddress.Uri;

                    if ((replyTo != null) && !replyTo.IsAnonymous && (localUri != replyTo.Uri))
                    {
                        string text = SR.Format(SR.SFxRequestHasInvalidReplyToOnClient, replyTo.Uri, localUri);
                        Exception error = new InvalidOperationException(text);
                        throw TraceUtility.ThrowHelperError(error, rpc.Request);
                    }

                    EndpointAddress faultTo = headers.FaultTo;
                    if ((faultTo != null) && !faultTo.IsAnonymous && (localUri != faultTo.Uri))
                    {
                        string text = SR.Format(SR.SFxRequestHasInvalidFaultToOnClient, faultTo.Uri, localUri);
                        Exception error = new InvalidOperationException(text);
                        throw TraceUtility.ThrowHelperError(error, rpc.Request);
                    }
                }
            }

            if (TraceUtility.MessageFlowTracingOnly)
            {
                //always set a new ID if none provided
            }

            if (rpc.Activity != null)
            {
                TraceUtility.SetActivity(rpc.Request, rpc.Activity);
                if (TraceUtility.ShouldPropagateActivity)
                {
                    TraceUtility.AddActivityHeader(rpc.Request);
                }
            }
            else if (TraceUtility.PropagateUserActivity || TraceUtility.ShouldPropagateActivity)
            {
                TraceUtility.AddAmbientActivityToMessage(rpc.Request);
            }
            operation.Parent.BeforeSendRequest(ref rpc);


            //Attach and transfer Activity
            if (FxTrace.Trace.IsEnd2EndActivityTracingEnabled)
            {
                TraceClientOperationPrepared(ref rpc);
            }

            TraceUtility.MessageFlowAtMessageSent(rpc.Request, rpc.EventTraceActivity);

            if (MessageLogger.LogMessagesAtServiceLevel)
            {
                MessageLogger.LogMessage(ref rpc.Request, (oneway ? MessageLoggingSource.ServiceLevelSendDatagram : MessageLoggingSource.ServiceLevelSendRequest) | MessageLoggingSource.LastChance);
            }
        }

        private void TraceClientOperationPrepared(ref ProxyRpc rpc)
        {
            //Retrieve the old id on the RPC and attach the id on the message since we have a message id now.
            Guid previousId = rpc.EventTraceActivity != null ? rpc.EventTraceActivity.ActivityId : Guid.Empty;
            EventTraceActivity requestActivity = EventTraceActivityHelper.TryExtractActivity(rpc.Request);
            if (requestActivity == null)
            {
                requestActivity = EventTraceActivity.GetFromThreadOrCreate();
                EventTraceActivityHelper.TryAttachActivity(rpc.Request, requestActivity);
            }
            rpc.EventTraceActivity = requestActivity;

            if (TD.ClientOperationPreparedIsEnabled())
            {
                string remoteAddress = string.Empty;
                if (this.RemoteAddress != null && this.RemoteAddress.Uri != null)
                {
                    remoteAddress = this.RemoteAddress.Uri.AbsoluteUri;
                }
                TD.ClientOperationPrepared(rpc.EventTraceActivity,
                                            rpc.Action,
                                            _clientRuntime.ContractName,
                                            remoteAddress,
                                            previousId);
            }
        }

        internal static IAsyncResult BeginCall(ServiceChannel channel, ProxyOperationRuntime operation, object[] ins, AsyncCallback callback, object asyncState)
        {
            Fx.Assert(channel != null, "'channel' MUST NOT be NULL.");
            Fx.Assert(operation != null, "'operation' MUST NOT be NULL.");
            return channel.BeginCall(operation.Action, operation.IsOneWay, operation, ins, channel._operationTimeout, callback, asyncState);
        }

        internal IAsyncResult BeginCall(string action, bool oneway, ProxyOperationRuntime operation, object[] ins, AsyncCallback callback, object asyncState)
        {
            return this.BeginCall(action, oneway, operation, ins, _operationTimeout, callback, asyncState);
        }

        internal IAsyncResult BeginCall(string action, bool oneway, ProxyOperationRuntime operation, object[] ins, TimeSpan timeout, AsyncCallback callback, object asyncState)
        {
            this.ThrowIfDisallowedInitializationUI();
            this.ThrowIfIdleAborted(operation);
            this.ThrowIfIsConnectionOpened(operation);

            ServiceModelActivity serviceModelActivity = null;

            if (DiagnosticUtility.ShouldUseActivity)
            {
                serviceModelActivity = ServiceModelActivity.CreateActivity(true);
                callback = TraceUtility.WrapExecuteUserCodeAsyncCallback(callback);
            }

            SendAsyncResult result;

            using (Activity boundOperation = ServiceModelActivity.BoundOperation(serviceModelActivity, true))
            {
                if (DiagnosticUtility.ShouldUseActivity)
                {
                    ServiceModelActivity.Start(serviceModelActivity, SR.Format(SR.ActivityProcessAction, action), ActivityType.ProcessAction);
                }

                result = new SendAsyncResult(this, operation, action, ins, oneway, timeout, callback, asyncState);
                if (DiagnosticUtility.ShouldUseActivity)
                {
                    result.Rpc.Activity = serviceModelActivity;
                }

                TraceServiceChannelCallStart(result.Rpc.EventTraceActivity, false);

                result.Begin();
            }

            return result;
        }

        internal object Call(string action, bool oneway, ProxyOperationRuntime operation, object[] ins, object[] outs)
        {
            return this.Call(action, oneway, operation, ins, outs, _operationTimeout);
        }

        internal object Call(string action, bool oneway, ProxyOperationRuntime operation, object[] ins, object[] outs, TimeSpan timeout)
        {
            this.ThrowIfDisallowedInitializationUI();
            this.ThrowIfIdleAborted(operation);
            this.ThrowIfIsConnectionOpened(operation);

            ProxyRpc rpc = new ProxyRpc(this, operation, action, ins, timeout);

            TraceServiceChannelCallStart(rpc.EventTraceActivity, true);

            using (rpc.Activity = DiagnosticUtility.ShouldUseActivity ? ServiceModelActivity.CreateBoundedActivity() : null)
            {
                if (DiagnosticUtility.ShouldUseActivity)
                {
                    ServiceModelActivity.Start(rpc.Activity, SR.Format(SR.ActivityProcessAction, action), ActivityType.ProcessAction);
                }

                this.PrepareCall(operation, oneway, ref rpc);

                if (!_explicitlyOpened)
                {
                    this.EnsureDisplayUI();
                    this.EnsureOpened(rpc.TimeoutHelper.RemainingTime());
                }
                else
                {
                    this.ThrowIfOpening();
                    this.ThrowIfDisposedOrNotOpen();
                }

                try
                {
                    ConcurrencyBehavior.UnlockInstanceBeforeCallout(OperationContext.Current);

                    if (oneway)
                    {
                        _binder.Send(rpc.Request, rpc.TimeoutHelper.RemainingTime());
                    }
                    else
                    {
                        rpc.Reply = _binder.Request(rpc.Request, rpc.TimeoutHelper.RemainingTime());

                        if (rpc.Reply == null)
                        {
                            this.ThrowIfFaulted();
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new CommunicationException(SR.SFxServerDidNotReply));
                        }
                    }
                }
                finally
                {
                    this.CompletedIOOperation();
                    CallOnceManager.SignalNextIfNonNull(_autoOpenManager);
                    ConcurrencyBehavior.LockInstanceAfterCallout(OperationContext.Current);
                }

                rpc.OutputParameters = outs;
                this.HandleReply(operation, ref rpc);
            }
            return rpc.ReturnValue;
        }

        internal object EndCall(string action, object[] outs, IAsyncResult result)
        {
            SendAsyncResult sendResult = result as SendAsyncResult;
            if (sendResult == null)
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new ArgumentException(SR.SFxInvalidCallbackIAsyncResult));

            using (ServiceModelActivity rpcActivity = sendResult.Rpc.Activity)
            {
                using (ServiceModelActivity.BoundOperation(rpcActivity, true))
                {
                    if (sendResult.Rpc.Activity != null && DiagnosticUtility.ShouldUseActivity)
                    {
                        sendResult.Rpc.Activity.Resume();
                    }
                    if (sendResult.Rpc.Channel != this)
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgument("result", SR.AsyncEndCalledOnWrongChannel);

                    if (action != MessageHeaders.WildcardAction && action != sendResult.Rpc.Action)
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgument("result", SR.AsyncEndCalledWithAnIAsyncResult);

                    SendAsyncResult.End(sendResult);

                    sendResult.Rpc.OutputParameters = outs;
                    this.HandleReply(sendResult.Rpc.Operation, ref sendResult.Rpc);

                    if (sendResult.Rpc.Activity != null)
                    {
                        sendResult.Rpc.Activity = null;
                    }
                    return sendResult.Rpc.ReturnValue;
                }
            }
        }

        internal void DecrementActivity()
        {
            int updatedActivityCount = Interlocked.Decrement(ref _activityCount);

            if (!((updatedActivityCount >= 0)))
            {
                throw Fx.AssertAndThrowFatal("ServiceChannel.DecrementActivity: (updatedActivityCount >= 0)");
            }

            if (updatedActivityCount == 0 && _autoClose)
            {
                try
                {
                    if (this.State == CommunicationState.Opened)
                    {
                        if (this.IsClient)
                        {
                            ISessionChannel<IDuplexSession> duplexSessionChannel = this.InnerChannel as ISessionChannel<IDuplexSession>;
                            if (duplexSessionChannel != null)
                            {
                                _hasChannelStartedAutoClosing = true;
                                duplexSessionChannel.Session.CloseOutputSession(this.CloseTimeout);
                            }
                        }
                        else
                        {
                            this.Close(this.CloseTimeout);
                        }
                    }
                }
                catch (CommunicationException)
                {
                }
                catch (TimeoutException e)
                {
                    if (TD.CloseTimeoutIsEnabled())
                    {
                        TD.CloseTimeout(e.Message);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        internal void FireUnknownMessageReceived(Message message)
        {
            EventHandler<UnknownMessageReceivedEventArgs> handler = _unknownMessageReceived;
            if (handler != null)
                handler(_proxy, new UnknownMessageReceivedEventArgs(message));
        }

        private TimeoutException GetOpenTimeoutException(TimeSpan timeout)
        {
            EndpointAddress address = this.RemoteAddress ?? this.LocalAddress;
            if (address != null)
            {
                return new TimeoutException(SR.Format(SR.TimeoutServiceChannelConcurrentOpen2, address, timeout));
            }
            else
            {
                return new TimeoutException(SR.Format(SR.TimeoutServiceChannelConcurrentOpen1, timeout));
            }
        }

        internal void HandleReceiveComplete(RequestContext context)
        {
            if (context == null && HasSession)
            {
                bool first;
                lock (this.ThisLock)
                {
                    first = !_doneReceiving;
                    _doneReceiving = true;
                }

                if (first)
                {
                    DispatchRuntime dispatchBehavior = this.ClientRuntime.DispatchRuntime;
                    this.DecrementActivity();
                }
            }
        }

        private void HandleReply(ProxyOperationRuntime operation, ref ProxyRpc rpc)
        {
            try
            {
                //set the ID after response
                if (TraceUtility.MessageFlowTracingOnly && rpc.ActivityId != Guid.Empty)
                {
                    System.Runtime.Diagnostics.DiagnosticTraceBase.ActivityId = rpc.ActivityId;
                }

                if (rpc.Reply != null)
                {
                    TraceUtility.MessageFlowAtMessageReceived(rpc.Reply, null, rpc.EventTraceActivity, false);

                    if (MessageLogger.LogMessagesAtServiceLevel)
                    {
                        MessageLogger.LogMessage(ref rpc.Reply, MessageLoggingSource.ServiceLevelReceiveReply | MessageLoggingSource.LastChance);
                    }
                    operation.Parent.AfterReceiveReply(ref rpc);

                    if ((operation.ReplyAction != MessageHeaders.WildcardAction) && !rpc.Reply.IsFault && rpc.Reply.Headers.Action != null)
                    {
                        if (String.CompareOrdinal(operation.ReplyAction, rpc.Reply.Headers.Action) != 0)
                        {
                            Exception error = new ProtocolException(SR.Format(SR.SFxReplyActionMismatch3,
                                                                                  operation.Name,
                                                                                  rpc.Reply.Headers.Action,
                                                                                  operation.ReplyAction));
                            this.TerminateIfNecessary(ref rpc);
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(error);
                        }
                    }
                    if (operation.DeserializeReply && _clientRuntime.IsFault(ref rpc.Reply))
                    {
                        MessageFault fault = MessageFault.CreateFault(rpc.Reply, _clientRuntime.MaxFaultSize);
                        string action = rpc.Reply.Headers.Action;
                        if (action == rpc.Reply.Version.Addressing.DefaultFaultAction)
                        {
                            action = null;
                        }
                        ThrowIfFaultUnderstood(rpc.Reply, fault, action, rpc.Reply.Version, rpc.Channel.GetProperty<FaultConverter>());
                        FaultException fe = rpc.Operation.FaultFormatter.Deserialize(fault, action);
                        this.TerminateIfNecessary(ref rpc);
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(fe);
                    }

                    operation.AfterReply(ref rpc);
                }
            }
            finally
            {
                if (operation.SerializeRequest)
                {
                    rpc.Request.Close();
                }

                OperationContext operationContext = OperationContext.Current;
                bool consumed = ((rpc.Reply != null) && (rpc.Reply.State != MessageState.Created));

                if ((operationContext != null) && operationContext.IsUserContext)
                {
                    operationContext.SetClientReply(rpc.Reply, consumed);
                }
                else if (consumed)
                {
                    rpc.Reply.Close();
                }

                if (TraceUtility.MessageFlowTracingOnly)
                {
                    if (rpc.ActivityId != Guid.Empty)
                    {
                        //reset the ID as it was created internally - ensures each call is uniquely correlatable
                        System.Runtime.Diagnostics.DiagnosticTraceBase.ActivityId = Guid.Empty;
                        rpc.ActivityId = Guid.Empty;
                    }
                }
            }
            this.TerminateIfNecessary(ref rpc);

            if (TD.ServiceChannelCallStopIsEnabled())
            {
                string remoteAddress = string.Empty;
                if (this.RemoteAddress != null && this.RemoteAddress.Uri != null)
                {
                    remoteAddress = this.RemoteAddress.Uri.AbsoluteUri;
                }
                TD.ServiceChannelCallStop(rpc.EventTraceActivity, rpc.Action,
                                            _clientRuntime.ContractName,
                                            remoteAddress);
            }
        }

        private void TerminateIfNecessary(ref ProxyRpc rpc)
        {
            if (rpc.Operation.IsTerminating)
            {
                _terminatingOperationName = rpc.Operation.Name;
                TerminatingOperationBehavior.AfterReply(ref rpc);
            }
        }

        private void ThrowIfFaultUnderstood(Message reply, MessageFault fault, string action, MessageVersion version, FaultConverter faultConverter)
        {
            Exception exception;
            if (faultConverter != null && faultConverter.TryCreateException(reply, fault, out exception))
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(exception);
            }

            bool checkSender;
            bool checkReceiver;
            FaultCode code;

            if (version.Envelope == EnvelopeVersion.Soap11)
            {
                checkSender = true;
                checkReceiver = true;
                code = fault.Code;
            }
            else
            {
                checkSender = fault.Code.IsSenderFault;
                checkReceiver = fault.Code.IsReceiverFault;
                code = fault.Code.SubCode;
            }

            if (code == null)
            {
                return;
            }

            if (code.Namespace == null)
            {
                return;
            }

            if (checkSender)
            {
                if (string.Compare(code.Namespace, FaultCodeConstants.Namespaces.NetDispatch, StringComparison.Ordinal) == 0)
                {
                    if (string.Compare(code.Name, FaultCodeConstants.Codes.SessionTerminated, StringComparison.Ordinal) == 0)
                    {
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new ChannelTerminatedException(fault.Reason.GetMatchingTranslation(CultureInfo.CurrentCulture).Text));
                    }

                    if (string.Compare(code.Name, FaultCodeConstants.Codes.TransactionAborted, StringComparison.Ordinal) == 0)
                    {
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new ProtocolException(fault.Reason.GetMatchingTranslation(CultureInfo.CurrentCulture).Text));
                    }
                }

                // throw SecurityAccessDeniedException explicitly
                if (string.Compare(code.Namespace, SecurityVersion.Default.HeaderNamespace.Value, StringComparison.Ordinal) == 0)
                {
                    if (string.Compare(code.Name, SecurityVersion.Default.FailedAuthenticationFaultCode.Value, StringComparison.Ordinal) == 0)
                    {
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new SecurityAccessDeniedException(fault.Reason.GetMatchingTranslation(CultureInfo.CurrentCulture).Text));
                    }
                }
            }

            if (checkReceiver)
            {
                if (string.Compare(code.Namespace, FaultCodeConstants.Namespaces.NetDispatch, StringComparison.Ordinal) == 0)
                {
                    if (string.Compare(code.Name, FaultCodeConstants.Codes.InternalServiceFault, StringComparison.Ordinal) == 0)
                    {
                        if (this.HasSession)
                        {
                            this.Fault();
                        }
                        if (fault.HasDetail)
                        {
                            ExceptionDetail detail = fault.GetDetail<ExceptionDetail>();
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new FaultException<ExceptionDetail>(detail, fault.Reason, fault.Code, action));
                        }
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new FaultException(fault, action));
                    }
                    if (string.Compare(code.Name, FaultCodeConstants.Codes.DeserializationFailed, StringComparison.Ordinal) == 0)
                    {
                        throw DiagnosticUtility.ExceptionUtility.ThrowHelperWarning(new ProtocolException(
                            fault.Reason.GetMatchingTranslation(CultureInfo.CurrentCulture).Text));
                    }
                }
            }
        }

        private void ThrowIfIdleAborted(ProxyOperationRuntime operation)
        {
            if (_idleManager != null && _idleManager.DidIdleAbort)
            {
                string text = SR.Format(SR.SFxServiceChannelIdleAborted, operation.Name);
                Exception error = new CommunicationObjectAbortedException(text);
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(error);
            }
        }

        private void ThrowIfIsConnectionOpened(ProxyOperationRuntime operation)
        {
            if (operation.IsSessionOpenNotificationEnabled)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(
                    SR.Format(SR.SFxServiceChannelCannotBeCalledBecauseIsSessionOpenNotificationEnabled, operation.Name, "Action", OperationDescription.SessionOpenedAction, "Open")));
            }
        }

        private void ThrowIfInitializationUINotCalled()
        {
            if (!_didInteractiveInitialization && (this.ClientRuntime.InteractiveChannelInitializers.Count > 0))
            {
                IInteractiveChannelInitializer example = this.ClientRuntime.InteractiveChannelInitializers[0];
                string text = SR.Format(SR.SFxInitializationUINotCalled, example.GetType().ToString());
                Exception error = new InvalidOperationException(text);
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(error);
            }
        }

        private void ThrowIfDisallowedInitializationUI()
        {
            if (!_allowInitializationUI)
            {
                this.ThrowIfDisallowedInitializationUICore();
            }
        }

        private void ThrowIfDisallowedInitializationUICore()
        {
            if (this.ClientRuntime.InteractiveChannelInitializers.Count > 0)
            {
                IInteractiveChannelInitializer example = this.ClientRuntime.InteractiveChannelInitializers[0];
                string text = SR.Format(SR.SFxInitializationUIDisallowed, example.GetType().ToString());
                Exception error = new InvalidOperationException(text);
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(error);
            }
        }

        private void ThrowIfOpening()
        {
            if (this.State == CommunicationState.Opening)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.SFxCannotCallAutoOpenWhenExplicitOpenCalled));
            }
        }

        internal void IncrementActivity()
        {
            Interlocked.Increment(ref _activityCount);
        }

        private void OnInnerChannelFaulted(object sender, EventArgs e)
        {
            this.Fault();

            if (this.HasSession)
            {
                DispatchRuntime dispatchRuntime = this.ClientRuntime.DispatchRuntime;
            }

            if (_autoClose && !this.IsClient)
            {
                this.Abort();
            }
        }

        private void AddMessageProperties(Message message, OperationContext context)
        {
            if (_allowOutputBatching)
            {
                message.Properties.AllowOutputBatching = true;
            }

            if (context != null && context.InternalServiceChannel == this)
            {
                if (!context.OutgoingMessageVersion.IsMatch(message.Headers.MessageVersion))
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(
                        SR.Format(SR.SFxVersionMismatchInOperationContextAndMessage2, context.OutgoingMessageVersion, message.Headers.MessageVersion)
                        ));
                }

                if (context.HasOutgoingMessageHeaders)
                {
                    message.Headers.CopyHeadersFrom(context.OutgoingMessageHeaders);
                }

                if (context.HasOutgoingMessageProperties)
                {
                    message.Properties.CopyProperties(context.OutgoingMessageProperties);
                }
            }
        }

        #region IChannel Members
        public void Send(Message message)
        {
            this.Send(message, this.OperationTimeout);
        }

        public void Send(Message message, TimeSpan timeout)
        {
            ProxyOperationRuntime operation = UnhandledProxyOperation;
            this.Call(message.Headers.Action, true, operation, new object[] { message }, Array.Empty<object>(), timeout);
        }

        public IAsyncResult BeginSend(Message message, AsyncCallback callback, object state)
        {
            return this.BeginSend(message, this.OperationTimeout, callback, state);
        }

        public IAsyncResult BeginSend(Message message, TimeSpan timeout, AsyncCallback callback, object state)
        {
            ProxyOperationRuntime operation = UnhandledProxyOperation;
            return this.BeginCall(message.Headers.Action, true, operation, new object[] { message }, timeout, callback, state);
        }

        public void EndSend(IAsyncResult result)
        {
            this.EndCall(MessageHeaders.WildcardAction, Array.Empty<object>(), result);
        }

        public Message Request(Message message)
        {
            return this.Request(message, this.OperationTimeout);
        }

        public Message Request(Message message, TimeSpan timeout)
        {
            ProxyOperationRuntime operation = UnhandledProxyOperation;
            return (Message)this.Call(message.Headers.Action, false, operation, new object[] { message }, Array.Empty<object>(), timeout);
        }

        public IAsyncResult BeginRequest(Message message, AsyncCallback callback, object state)
        {
            return this.BeginRequest(message, this.OperationTimeout, callback, state);
        }

        public IAsyncResult BeginRequest(Message message, TimeSpan timeout, AsyncCallback callback, object state)
        {
            ProxyOperationRuntime operation = this.UnhandledProxyOperation;
            return this.BeginCall(message.Headers.Action, false, operation, new object[] { message }, timeout, callback, state);
        }

        public Message EndRequest(IAsyncResult result)
        {
            return (Message)this.EndCall(MessageHeaders.WildcardAction, Array.Empty<object>(), result);
        }

        protected override void OnAbort()
        {
            if (_idleManager != null)
            {
                _idleManager.CancelTimer();
            }

            _binder.Abort();

            if (_factory != null)
                _factory.ChannelDisposed(this);

            if (_closeFactory)
            {
                if (_factory != null)
                    _factory.Abort();
            }

            CleanupChannelCollections();
        }

        protected override IAsyncResult OnBeginClose(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return CommunicationObjectInternal.OnBeginClose(this, timeout, callback, state);
        }

        protected override void OnEndClose(IAsyncResult result)
        {
            CommunicationObjectInternal.OnEnd(result);
        }

        protected override void OnClose(TimeSpan timeout)
        {
            CommunicationObjectInternal.OnClose(this, timeout);
        }

        protected internal override async Task OnCloseAsync(TimeSpan timeout)
        {
            TimeoutHelper timeoutHelper = new TimeoutHelper(timeout);

            if (_idleManager != null)
            {
                _idleManager.CancelTimer();
            }

            if (_factory != null)
            {
                _factory.ChannelDisposed(this);
            }

            if (_closeBinder)
            {
                var asyncInnerChannel = this.InnerChannel as IAsyncCommunicationObject;
                if (asyncInnerChannel != null)
                {
                    await asyncInnerChannel.CloseAsync(timeoutHelper.RemainingTime());
                }
                else
                {
                    this.InnerChannel.Close(timeoutHelper.RemainingTime());
                }
            }

            if (_closeFactory)
            {
                var asyncFactory = _factory as IAsyncCommunicationObject;
                if (asyncFactory != null)
                {
                    await asyncFactory.CloseAsync(timeoutHelper.RemainingTime());
                }
                else
                {
                    _factory.Close(timeoutHelper.RemainingTime());
                }
            }

            CleanupChannelCollections();
        }

        protected override IAsyncResult OnBeginOpen(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return CommunicationObjectInternal.OnBeginOpen(this, timeout, callback, state);
        }

        protected override void OnEndOpen(IAsyncResult result)
        {
            CommunicationObjectInternal.OnEnd(result);
        }

        protected override void OnOpen(TimeSpan timeout)
        {
            CommunicationObjectInternal.OnOpen(this, timeout);
        }

        protected internal override async Task OnOpenAsync(TimeSpan timeout)
        {
            this.ThrowIfDisallowedInitializationUI();
            this.ThrowIfInitializationUINotCalled();

            if (_autoOpenManager == null)
            {
                _explicitlyOpened = true;
            }

            this.TraceChannelOpenStarted();

            if (_openBinder)
            {
                var asyncInnerChannel = this.InnerChannel as IAsyncCommunicationObject;
                if (asyncInnerChannel != null)
                {
                    await asyncInnerChannel.OpenAsync(timeout);
                }
                else
                {
                    this.InnerChannel.Open(timeout);
                }
            }

            this.BindDuplexCallbacks();
            this.CompletedIOOperation();

            this.TraceChannelOpenCompleted();
        }

        private void CleanupChannelCollections()
        {
            if (!_hasCleanedUpChannelCollections)
            {
                lock (this.ThisLock)
                {
                    if (!_hasCleanedUpChannelCollections)
                    {
                        if (this.InstanceContext != null)
                        {
                            this.InstanceContext.OutgoingChannels.Remove((IChannel)_proxy);
                        }

                        _hasCleanedUpChannelCollections = true;
                    }
                }
            }
        }

        #endregion

        #region IClientChannel Members

        bool IDuplexContextChannel.AutomaticInputSessionShutdown
        {
            get { return _autoClose; }
            set { _autoClose = value; }
        }

        bool IClientChannel.AllowInitializationUI
        {
            get { return _allowInitializationUI; }
            set
            {
                this.ThrowIfDisposedOrImmutable();
                _allowInitializationUI = value;
            }
        }

        bool IContextChannel.AllowOutputBatching
        {
            get { return _allowOutputBatching; }
            set { _allowOutputBatching = value; }
        }

        bool IClientChannel.DidInteractiveInitialization
        {
            get { return _didInteractiveInitialization; }
        }

        IAsyncResult IDuplexContextChannel.BeginCloseOutputSession(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return GetDuplexSessionOrThrow().BeginCloseOutputSession(timeout, callback, state);
        }

        void IDuplexContextChannel.EndCloseOutputSession(IAsyncResult result)
        {
            GetDuplexSessionOrThrow().EndCloseOutputSession(result);
        }

        void IDuplexContextChannel.CloseOutputSession(TimeSpan timeout)
        {
            GetDuplexSessionOrThrow().CloseOutputSession(timeout);
        }

        private IDuplexSession GetDuplexSessionOrThrow()
        {
            if (this.InnerChannel == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.channelIsNotAvailable0));
            }

            ISessionChannel<IDuplexSession> duplexSessionChannel = this.InnerChannel as ISessionChannel<IDuplexSession>;
            if (duplexSessionChannel == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new InvalidOperationException(SR.channelDoesNotHaveADuplexSession0));
            }

            return duplexSessionChannel.Session;
        }

        IExtensionCollection<IContextChannel> IExtensibleObject<IContextChannel>.Extensions
        {
            get
            {
                lock (this.ThisLock)
                {
                    if (_extensions == null)
                        _extensions = new ExtensionCollection<IContextChannel>((IContextChannel)this.Proxy, this.ThisLock);
                    return _extensions;
                }
            }
        }

        InstanceContext IDuplexContextChannel.CallbackInstance
        {
            get { return _instanceContext; }
            set
            {
                lock (this.ThisLock)
                {
                    if (_instanceContext != null)
                    {
                        _instanceContext.OutgoingChannels.Remove((IChannel)_proxy);
                    }

                    _instanceContext = value;

                    if (_instanceContext != null)
                    {
                        _instanceContext.OutgoingChannels.Add((IChannel)_proxy);
                    }
                }
            }
        }

        IInputSession IContextChannel.InputSession
        {
            get
            {
                if (this.InnerChannel != null)
                {
                    ISessionChannel<IInputSession> inputSession = this.InnerChannel as ISessionChannel<IInputSession>;
                    if (inputSession != null)
                        return inputSession.Session;

                    ISessionChannel<IDuplexSession> duplexSession = this.InnerChannel as ISessionChannel<IDuplexSession>;
                    if (duplexSession != null)
                        return duplexSession.Session;
                }

                return null;
            }
        }

        IOutputSession IContextChannel.OutputSession
        {
            get
            {
                if (this.InnerChannel != null)
                {
                    ISessionChannel<IOutputSession> outputSession = this.InnerChannel as ISessionChannel<IOutputSession>;
                    if (outputSession != null)
                        return outputSession.Session;

                    ISessionChannel<IDuplexSession> duplexSession = this.InnerChannel as ISessionChannel<IDuplexSession>;
                    if (duplexSession != null)
                        return duplexSession.Session;
                }

                return null;
            }
        }

        string IContextChannel.SessionId
        {
            get
            {
                if (this.InnerChannel != null)
                {
                    ISessionChannel<IInputSession> inputSession = this.InnerChannel as ISessionChannel<IInputSession>;
                    if (inputSession != null)
                        return inputSession.Session.Id;

                    ISessionChannel<IOutputSession> outputSession = this.InnerChannel as ISessionChannel<IOutputSession>;
                    if (outputSession != null)
                        return outputSession.Session.Id;

                    ISessionChannel<IDuplexSession> duplexSession = this.InnerChannel as ISessionChannel<IDuplexSession>;
                    if (duplexSession != null)
                        return duplexSession.Session.Id;
                }

                return null;
            }
        }

        event EventHandler<UnknownMessageReceivedEventArgs> IClientChannel.UnknownMessageReceived
        {
            add
            {
                lock (this.ThisLock)
                {
                    _unknownMessageReceived += value;
                }
            }
            remove
            {
                lock (this.ThisLock)
                {
                    _unknownMessageReceived -= value;
                }
            }
        }

        public void DisplayInitializationUI()
        {
            this.ThrowIfDisallowedInitializationUI();

            if (_autoDisplayUIManager == null)
            {
                _explicitlyOpened = true;
            }

            this.ClientRuntime.GetRuntime().DisplayInitializationUI(this);
            _didInteractiveInitialization = true;
        }

        public IAsyncResult BeginDisplayInitializationUI(AsyncCallback callback, object state)
        {
            this.ThrowIfDisallowedInitializationUI();

            if (_autoDisplayUIManager == null)
            {
                _explicitlyOpened = true;
            }

            return this.ClientRuntime.GetRuntime().BeginDisplayInitializationUI(this, callback, state);
        }

        public void EndDisplayInitializationUI(IAsyncResult result)
        {
            this.ClientRuntime.GetRuntime().EndDisplayInitializationUI(result);
            _didInteractiveInitialization = true;
        }

        void IDisposable.Dispose()
        {
            this.Close();
        }

        #endregion

        private void TraceChannelOpenStarted()
        {
            if (TD.ClientChannelOpenStartIsEnabled() && _endpointDispatcher == null)
            {
                TD.ClientChannelOpenStart(this.EventActivity);
            }
            else if (TD.ServiceChannelOpenStartIsEnabled())
            {
                TD.ServiceChannelOpenStart(this.EventActivity);
            }
        }

        private void TraceChannelOpenCompleted()
        {
            if (_endpointDispatcher == null && TD.ClientChannelOpenStopIsEnabled())
            {
                TD.ClientChannelOpenStop(this.EventActivity);
            }
            else if (TD.ServiceChannelOpenStopIsEnabled())
            {
                TD.ServiceChannelOpenStop(this.EventActivity);
            }
        }

        private static void TraceServiceChannelCallStart(EventTraceActivity eventTraceActivity, bool isSynchronous)
        {
            if (TD.ServiceChannelCallStartIsEnabled())
            {
                if (isSynchronous)
                {
                    TD.ServiceChannelCallStart(eventTraceActivity);
                }
                else
                {
                    TD.ServiceChannelBeginCallStart(eventTraceActivity);
                }
            }
        }

        // Invariants for signalling the CallOnce manager.
        //
        // 1) If a Call, BeginCall, or EndCall on the channel throws,
        //    the manager will SignalNext itself.
        // 2) If a Waiter times out, it will SignalNext its manager
        //    once it is both timed out and signalled.
        // 3) Once Call or EndCall returns successfully, it guarantees
        //    that SignalNext will be called once the // next stage
        //    has sufficiently completed.

        internal class SendAsyncResult : TraceAsyncResult
        {
            private readonly bool _isOneWay;
            private readonly ProxyOperationRuntime _operation;
            internal ProxyRpc Rpc;
            private OperationContext _operationContext;

            private static AsyncCallback s_ensureInteractiveInitCallback = Fx.ThunkCallback(EnsureInteractiveInitCallback);
            private static AsyncCallback s_ensureOpenCallback = Fx.ThunkCallback(EnsureOpenCallback);
            private static AsyncCallback s_sendCallback = Fx.ThunkCallback(SendCallback);

            internal SendAsyncResult(ServiceChannel channel, ProxyOperationRuntime operation,
                                     string action, object[] inputParameters, bool isOneWay, TimeSpan timeout,
                                     AsyncCallback userCallback, object userState)
                : base(userCallback, userState)
            {
                this.Rpc = new ProxyRpc(channel, operation, action, inputParameters, timeout);
                _isOneWay = isOneWay;
                _operation = operation;
                _operationContext = OperationContext.Current;
            }

            internal void Begin()
            {
                this.Rpc.Channel.PrepareCall(_operation, _isOneWay, ref this.Rpc);

                if (this.Rpc.Channel._explicitlyOpened)
                {
                    this.Rpc.Channel.ThrowIfOpening();
                    this.Rpc.Channel.ThrowIfDisposedOrNotOpen();
                    this.StartSend(true);
                }
                else
                {
                    this.StartEnsureInteractiveInit();
                }
            }

            private void StartEnsureInteractiveInit()
            {
                IAsyncResult result = this.Rpc.Channel.BeginEnsureDisplayUI(s_ensureInteractiveInitCallback, this);

                if (result.CompletedSynchronously)
                {
                    this.FinishEnsureInteractiveInit(result, true);
                }
            }

            private static void EnsureInteractiveInitCallback(IAsyncResult result)
            {
                if (!result.CompletedSynchronously)
                {
                    ((SendAsyncResult)result.AsyncState).FinishEnsureInteractiveInit(result, false);
                }
            }

            private void FinishEnsureInteractiveInit(IAsyncResult result, bool completedSynchronously)
            {
                Exception exception = null;

                try
                {
                    this.Rpc.Channel.EndEnsureDisplayUI(result);
                }
                catch (Exception e)
                {
                    if (Fx.IsFatal(e) || completedSynchronously)
                    {
                        throw;
                    }
                    exception = e;
                }

                if (exception != null)
                {
                    this.CallComplete(completedSynchronously, exception);
                }
                else
                {
                    this.StartEnsureOpen(completedSynchronously);
                }
            }

            private void StartEnsureOpen(bool completedSynchronously)
            {
                TimeSpan timeout = this.Rpc.TimeoutHelper.RemainingTime();
                IAsyncResult result = null;
                Exception exception = null;

                try
                {
                    result = this.Rpc.Channel.BeginEnsureOpened(timeout, s_ensureOpenCallback, this);
                }
                catch (Exception e)
                {
                    if (Fx.IsFatal(e) || completedSynchronously)
                    {
                        throw;
                    }
                    exception = e;
                }

                if (exception != null)
                {
                    this.CallComplete(completedSynchronously, exception);
                }
                else if (result.CompletedSynchronously)
                {
                    this.FinishEnsureOpen(result, completedSynchronously);
                }
            }

            private static void EnsureOpenCallback(IAsyncResult result)
            {
                if (!result.CompletedSynchronously)
                {
                    ((SendAsyncResult)result.AsyncState).FinishEnsureOpen(result, false);
                }
            }

            private void FinishEnsureOpen(IAsyncResult result, bool completedSynchronously)
            {
                Exception exception = null;
                using (ServiceModelActivity.BoundOperation(this.Rpc.Activity))
                {
                    try
                    {
                        this.Rpc.Channel.EndEnsureOpened(result);
                    }
                    catch (Exception e)
                    {
                        if (Fx.IsFatal(e) || completedSynchronously)
                        {
                            throw;
                        }
                        exception = e;
                    }

                    if (exception != null)
                    {
                        this.CallComplete(completedSynchronously, exception);
                    }
                    else
                    {
                        this.StartSend(completedSynchronously);
                    }
                }
            }

            private void StartSend(bool completedSynchronously)
            {
                TimeSpan timeout = this.Rpc.TimeoutHelper.RemainingTime();
                IAsyncResult result = null;
                Exception exception = null;

                try
                {
                    ConcurrencyBehavior.UnlockInstanceBeforeCallout(_operationContext);

                    if (_isOneWay)
                    {
                        result = this.Rpc.Channel._binder.BeginSend(this.Rpc.Request, timeout, s_sendCallback, this);
                    }
                    else
                    {
                        result = this.Rpc.Channel._binder.BeginRequest(this.Rpc.Request, timeout, s_sendCallback, this);
                    }
                }
                catch (Exception e)
                {
                    if (Fx.IsFatal(e))
                    {
                        throw;
                    }
                    if (completedSynchronously)
                    {
                        ConcurrencyBehavior.LockInstanceAfterCallout(_operationContext);
                        throw;
                    }
                    exception = e;
                }
                finally
                {
                    CallOnceManager.SignalNextIfNonNull(this.Rpc.Channel._autoOpenManager);
                }

                if (exception != null)
                {
                    this.CallComplete(completedSynchronously, exception);
                }
                else if (result.CompletedSynchronously)
                {
                    this.FinishSend(result, completedSynchronously);
                }
            }

            private static void SendCallback(IAsyncResult result)
            {
                if (!result.CompletedSynchronously)
                {
                    ((SendAsyncResult)result.AsyncState).FinishSend(result, false);
                }
            }

            private void FinishSend(IAsyncResult result, bool completedSynchronously)
            {
                Exception exception = null;

                try
                {
                    if (_isOneWay)
                    {
                        this.Rpc.Channel._binder.EndSend(result);
                    }
                    else
                    {
                        this.Rpc.Reply = this.Rpc.Channel._binder.EndRequest(result);

                        if (this.Rpc.Reply == null)
                        {
                            this.Rpc.Channel.ThrowIfFaulted();
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new CommunicationException(SR.SFxServerDidNotReply));
                        }
                    }
                }
                catch (Exception e)
                {
                    if (Fx.IsFatal(e))
                    {
                        throw;
                    }
                    if (completedSynchronously)
                    {
                        ConcurrencyBehavior.LockInstanceAfterCallout(_operationContext);
                        throw;
                    }
                    exception = e;
                }

                this.CallComplete(completedSynchronously, exception);
            }

            private void CallComplete(bool completedSynchronously, Exception exception)
            {
                this.Rpc.Channel.CompletedIOOperation();
                this.Complete(completedSynchronously, exception);
            }

            public static void End(SendAsyncResult result)
            {
                try
                {
                    AsyncResult.End<SendAsyncResult>(result);
                }
                finally
                {
                    ConcurrencyBehavior.LockInstanceAfterCallout(result._operationContext);
                }
            }
        }

        internal interface ICallOnce
        {
            void Call(ServiceChannel channel, TimeSpan timeout);
            IAsyncResult BeginCall(ServiceChannel channel, TimeSpan timeout, AsyncCallback callback, object state);
            void EndCall(ServiceChannel channel, IAsyncResult result);
        }

        internal class CallDisplayUIOnce : ICallOnce
        {
            private static CallDisplayUIOnce s_instance;

            internal static CallDisplayUIOnce Instance
            {
                get
                {
                    if (CallDisplayUIOnce.s_instance == null)
                    {
                        CallDisplayUIOnce.s_instance = new CallDisplayUIOnce();
                    }
                    return CallDisplayUIOnce.s_instance;
                }
            }
            [Conditional("DEBUG")]

            private void ValidateTimeoutIsMaxValue(TimeSpan timeout)
            {
                if (timeout != TimeSpan.MaxValue)
                {
                    Fx.Assert("non-MaxValue timeout for displaying interactive initialization UI");
                }
            }

            void ICallOnce.Call(ServiceChannel channel, TimeSpan timeout)
            {
                this.ValidateTimeoutIsMaxValue(timeout);
                channel.DisplayInitializationUI();
            }

            IAsyncResult ICallOnce.BeginCall(ServiceChannel channel, TimeSpan timeout, AsyncCallback callback, object state)
            {
                this.ValidateTimeoutIsMaxValue(timeout);
                return channel.BeginDisplayInitializationUI(callback, state);
            }

            void ICallOnce.EndCall(ServiceChannel channel, IAsyncResult result)
            {
                channel.EndDisplayInitializationUI(result);
            }
        }

        internal class CallOpenOnce : ICallOnce
        {
            private static CallOpenOnce s_instance;

            internal static CallOpenOnce Instance
            {
                get
                {
                    if (CallOpenOnce.s_instance == null)
                    {
                        CallOpenOnce.s_instance = new CallOpenOnce();
                    }
                    return CallOpenOnce.s_instance;
                }
            }

            void ICallOnce.Call(ServiceChannel channel, TimeSpan timeout)
            {
                channel.Open(timeout);
            }

            IAsyncResult ICallOnce.BeginCall(ServiceChannel channel, TimeSpan timeout, AsyncCallback callback, object state)
            {
                return channel.BeginOpen(timeout, callback, state);
            }

            void ICallOnce.EndCall(ServiceChannel channel, IAsyncResult result)
            {
                channel.EndOpen(result);
            }
        }

        internal class CallOnceManager
        {
            private readonly ICallOnce _callOnce;
            private readonly ServiceChannel _channel;
            private bool _isFirst = true;
            private Queue<IWaiter> _queue;

            private static Action<object> s_signalWaiter = new Action<object>(CallOnceManager.SignalWaiter);

            internal CallOnceManager(ServiceChannel channel, ICallOnce callOnce)
            {
                _callOnce = callOnce;
                _channel = channel;
                _queue = new Queue<IWaiter>();
            }

            private object ThisLock
            {
                get { return this; }
            }

            internal void CallOnce(TimeSpan timeout, CallOnceManager cascade)
            {
                SyncWaiter waiter = null;
                bool first = false;

                if (_queue != null)
                {
                    lock (this.ThisLock)
                    {
                        if (_queue != null)
                        {
                            if (_isFirst)
                            {
                                first = true;
                                _isFirst = false;
                            }
                            else
                            {
                                waiter = new SyncWaiter(this);
                                _queue.Enqueue(waiter);
                            }
                        }
                    }
                }

                CallOnceManager.SignalNextIfNonNull(cascade);

                if (first)
                {
                    bool throwing = true;
                    try
                    {
                        _callOnce.Call(_channel, timeout);
                        throwing = false;
                    }
                    finally
                    {
                        if (throwing)
                        {
                            this.SignalNext();
                        }
                    }
                }
                else if (waiter != null)
                {
                    waiter.Wait(timeout);
                }
            }

            internal IAsyncResult BeginCallOnce(TimeSpan timeout, CallOnceManager cascade,
                                                AsyncCallback callback, object state)
            {
                AsyncWaiter waiter = null;
                bool first = false;

                if (_queue != null)
                {
                    lock (this.ThisLock)
                    {
                        if (_queue != null)
                        {
                            if (_isFirst)
                            {
                                first = true;
                                _isFirst = false;
                            }
                            else
                            {
                                waiter = new AsyncWaiter(this, timeout, callback, state);
                                _queue.Enqueue(waiter);
                            }
                        }
                    }
                }

                CallOnceManager.SignalNextIfNonNull(cascade);

                if (first)
                {
                    bool throwing = true;
                    try
                    {
                        IAsyncResult result = _callOnce.BeginCall(_channel, timeout, callback, state);
                        throwing = false;
                        return result;
                    }
                    finally
                    {
                        if (throwing)
                        {
                            this.SignalNext();
                        }
                    }
                }
                else if (waiter != null)
                {
                    return waiter;
                }
                else
                {
                    return new CallOnceCompletedAsyncResult(callback, state);
                }
            }

            internal void EndCallOnce(IAsyncResult result)
            {
                if (result is CallOnceCompletedAsyncResult)
                {
                    CallOnceCompletedAsyncResult.End(result);
                }
                else if (result is AsyncWaiter)
                {
                    AsyncWaiter.End(result);
                }
                else
                {
                    bool throwing = true;
                    try
                    {
                        _callOnce.EndCall(_channel, result);
                        throwing = false;
                    }
                    finally
                    {
                        if (throwing)
                        {
                            this.SignalNext();
                        }
                    }
                }
            }

            static internal void SignalNextIfNonNull(CallOnceManager manager)
            {
                if (manager != null)
                {
                    manager.SignalNext();
                }
            }

            internal void SignalNext()
            {
                if (_queue == null)
                {
                    return;
                }

                IWaiter waiter = null;

                lock (this.ThisLock)
                {
                    if (_queue != null)
                    {
                        if (_queue.Count > 0)
                        {
                            waiter = _queue.Dequeue();
                        }
                        else
                        {
                            _queue = null;
                        }
                    }
                }

                if (waiter != null)
                {
                    ActionItem.Schedule(CallOnceManager.s_signalWaiter, waiter);
                }
            }

            private static void SignalWaiter(object state)
            {
                ((IWaiter)state).Signal();
            }

            private interface IWaiter
            {
                void Signal();
            }

            internal class SyncWaiter : IWaiter
            {
                private ManualResetEvent _wait = new ManualResetEvent(false);
                private CallOnceManager _manager;
                private bool _isTimedOut = false;
                private bool _isSignaled = false;
                private int _waitCount = 0;

                internal SyncWaiter(CallOnceManager manager)
                {
                    _manager = manager;
                }

                private bool ShouldSignalNext
                {
                    get { return _isTimedOut && _isSignaled; }
                }

                void IWaiter.Signal()
                {
                    _wait.Set();
                    this.CloseWaitHandle();

                    bool signalNext;
                    lock (_manager.ThisLock)
                    {
                        _isSignaled = true;
                        signalNext = this.ShouldSignalNext;
                    }
                    if (signalNext)
                    {
                        _manager.SignalNext();
                    }
                }

                internal bool Wait(TimeSpan timeout)
                {
                    try
                    {
                        if (!TimeoutHelper.WaitOne(_wait, timeout))
                        {
                            bool signalNext;
                            lock (_manager.ThisLock)
                            {
                                _isTimedOut = true;
                                signalNext = this.ShouldSignalNext;
                            }
                            if (signalNext)
                            {
                                _manager.SignalNext();
                            }
                        }
                    }
                    finally
                    {
                        this.CloseWaitHandle();
                    }

                    return !_isTimedOut;
                }

                private void CloseWaitHandle()
                {
                    if (Interlocked.Increment(ref _waitCount) == 2)
                    {
                        _wait.Dispose();
                    }
                }
            }

            internal class AsyncWaiter : AsyncResult, IWaiter
            {
                private static Action<object> s_timerCallback = new Action<object>(AsyncWaiter.TimerCallback);

                private CallOnceManager _manager;
                private TimeSpan _timeout;
                private Timer _timer;

                internal AsyncWaiter(CallOnceManager manager, TimeSpan timeout,
                                     AsyncCallback callback, object state)
                    : base(callback, state)
                {
                    _manager = manager;
                    _timeout = timeout;

                    if (timeout != TimeSpan.MaxValue)
                    {
                        _timer = new Timer(new TimerCallback(s_timerCallback), this, timeout, TimeSpan.FromMilliseconds(-1));
                    }
                }

                internal static void End(IAsyncResult result)
                {
                    AsyncResult.End<AsyncWaiter>(result);
                }

                void IWaiter.Signal()
                {
                    if ((_timer == null) || _timer.Change(TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(-1)))
                    {
                        this.Complete(false);
                        _manager._channel.Closed -= this.OnClosed;
                    }
                    else
                    {
                        _manager.SignalNext();
                    }
                }

                private void OnClosed(object sender, EventArgs e)
                {
                    if ((_timer == null) || _timer.Change(TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(-1)))
                    {
                        this.Complete(false, _manager._channel.CreateClosedException());
                    }
                }

                private static void TimerCallback(object state)
                {
                    AsyncWaiter _this = (AsyncWaiter)state;
                    _this.Complete(false, _this._manager._channel.GetOpenTimeoutException(_this._timeout));
                }
            }
        }

        private class CallOnceCompletedAsyncResult : AsyncResult
        {
            internal CallOnceCompletedAsyncResult(AsyncCallback callback, object state)
                : base(callback, state)
            {
                this.Complete(true);
            }

            static internal void End(IAsyncResult result)
            {
                AsyncResult.End<CallOnceCompletedAsyncResult>(result);
            }
        }

        internal class SessionIdleManager
        {
            private readonly IChannelBinder _binder;
            private ServiceChannel _channel;
            private readonly long _idleTicks;
            private long _lastActivity;
            private readonly Timer _timer;
            private static Action<object> s_timerCallback;
            private bool _didIdleAbort;
            private bool _isTimerCancelled;
            private object _thisLock;

            private SessionIdleManager(IChannelBinder binder, TimeSpan idle)
            {
                _binder = binder;
                _timer = new Timer(new TimerCallback(GetTimerCallback()), this, idle, TimeSpan.FromMilliseconds(-1));
                _idleTicks = Ticks.FromTimeSpan(idle);
                _thisLock = new Object();
            }

            internal static SessionIdleManager CreateIfNeeded(IChannelBinder binder, TimeSpan idle)
            {
                if (binder.HasSession && (idle != TimeSpan.MaxValue))
                {
                    return new SessionIdleManager(binder, idle);
                }
                else
                {
                    return null;
                }
            }

            internal bool DidIdleAbort
            {
                get
                {
                    lock (_thisLock)
                    {
                        return _didIdleAbort;
                    }
                }
            }

            internal void CancelTimer()
            {
                lock (_thisLock)
                {
                    _isTimerCancelled = true;
                    _timer.Change(TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(-1));
                }
            }

            internal void CompletedActivity()
            {
                Interlocked.Exchange(ref _lastActivity, Ticks.Now);
            }

            internal void RegisterChannel(ServiceChannel channel, out bool didIdleAbort)
            {
                lock (_thisLock)
                {
                    _channel = channel;
                    didIdleAbort = _didIdleAbort;
                }
            }

            private static Action<object> GetTimerCallback()
            {
                if (SessionIdleManager.s_timerCallback == null)
                {
                    SessionIdleManager.s_timerCallback = SessionIdleManager.TimerCallback;
                }
                return SessionIdleManager.s_timerCallback;
            }

            private static void TimerCallback(object state)
            {
                ((SessionIdleManager)state).TimerCallback();
            }

            private void TimerCallback()
            {
                // This reads lastActivity atomically without changing its value.
                // (it only sets if it is zero, and then it sets it to zero).
                long last = Interlocked.CompareExchange(ref _lastActivity, 0, 0);
                long abortTime = last + _idleTicks;

                lock (_thisLock)
                {
                    long ticksNow = Ticks.Now;
                    if (ticksNow > abortTime)
                    {
                        if (TD.SessionIdleTimeoutIsEnabled())
                        {
                            string listenUri = string.Empty;
                            if (_binder.ListenUri != null)
                            {
                                listenUri = _binder.ListenUri.AbsoluteUri;
                            }

                            TD.SessionIdleTimeout(listenUri);
                        }

                        _didIdleAbort = true;
                        if (_channel != null)
                        {
                            _channel.Abort();
                        }
                        else
                        {
                            _binder.Abort();
                        }
                    }
                    else
                    {
                        if (!_isTimerCancelled && _binder.Channel.State != CommunicationState.Faulted && _binder.Channel.State != CommunicationState.Closed)
                        {
                            _timer.Change(Ticks.ToTimeSpan(abortTime - ticksNow), TimeSpan.FromMilliseconds(-1));
                        }
                    }
                }
            }
        }
    }
}
