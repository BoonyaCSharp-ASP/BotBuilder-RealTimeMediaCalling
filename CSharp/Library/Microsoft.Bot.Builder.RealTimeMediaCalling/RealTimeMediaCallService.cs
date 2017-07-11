﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK GitHub:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Calling.Events;
using Microsoft.Bot.Builder.Calling.Exceptions;
using Microsoft.Bot.Builder.Calling.ObjectModel.Contracts;
using Microsoft.Bot.Builder.Calling.ObjectModel.Misc;
using Microsoft.Bot.Builder.RealTimeMediaCalling.Events;
using Microsoft.Bot.Builder.RealTimeMediaCalling.ObjectModel.Contracts;
using Microsoft.Bot.Builder.RealTimeMediaCalling.ObjectModel.Misc;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace Microsoft.Bot.Builder.RealTimeMediaCalling
{
    internal class RealTimeMediaCallServiceParameters {
        /// <summary>
        /// Id for this call
        /// </summary>
        public string CallLegId { get; }

        /// <summary>
        /// CorrelationId for this call.
        /// </summary>
        public string CorrelationId { get; }

        public RealTimeMediaCallServiceParameters(string callLegId, string correlationId)
        {
            if (null == callLegId)
            {
                throw new ArgumentNullException(nameof(callLegId));
            }

            if (null == correlationId)
            {
                throw new ArgumentNullException(nameof(correlationId));
            }

            CallLegId = callLegId;
            CorrelationId = correlationId;
        }
    }

    /// <summary>
    /// Service that handles per call requests
    /// </summary>            
    internal class RealTimeMediaCallService : IInternalRealTimeMediaCallService
    {
        private readonly Uri _callbackUrl;
        private readonly Uri _notificationUrl;
        private Uri _subscriptionLink;
        private Uri _callLink;
        private Uri _placeCallUrl;
        private Timer _timer;
        private const int CallExpiredTimerInterval = 1000 * 60 * 10; //10 minutes

        private string _botId;
        private string _botSecret;
        private string _botToken;

        private readonly Uri _defaultPlaceCallEndpointUrl = new Uri("https://pma.plat.skype.com:6448/platform/v1/calls");
        /// <summary>
        /// Id for this call
        /// </summary>
        public string CallLegId { get; }

        /// <summary>
        /// CorrelationId for this call.
        /// </summary>
        public string CorrelationId { get; }

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        /// <value>
        /// Automatically injected by Autofac DI
        /// </value>
        public IRealTimeMediaLogger Logger { get; set; }


        /// <summary>
        /// Event raised when specified workflow fails to be validated by Bot platform
        /// </summary>
        public event Func<RealTimeMediaWorkflowValidationOutcomeEvent, Task> OnWorkflowValidationFailed;

        /// <summary>
        /// Event raised when bot receives incoming call
        /// </summary>
        public event Func<RealTimeMediaIncomingCallEvent, Task> OnIncomingCallReceived;

        /// <summary>
        /// Event raised when the bot gets the outcome of AnswerAppHostedMedia action and the call is established.
        /// </summary>
        public event Func<AnswerAppHostedMediaOutcomeEvent, Task> OnAnswerSucceeded;

        /// <summary>
        /// Event raised when the bot gets the outcome of AnswerAppHostedMedia action but the call failed.
        /// </summary>
        public event Func<AnswerAppHostedMediaOutcomeEvent, Task> OnAnswerFailed;

        /// <summary>
        /// Event raised when the bot requests to join a call
        /// </summary>
        public event Func<RealTimeMediaJoinCallEvent, Task> OnJoinCallRequested;

        /// <summary>
        /// Event raised when the bot gets the outcome of JoinCallAppHostedMedia action and the call is established.
        /// </summary>
        public event Func<JoinCallAppHostedMediaOutcomeEvent, Task> OnJoinCallSucceeded;

        /// <summary>
        /// Event raised when the bot gets the outcome of JoinCallAppHostedMedia action but the call failed.
        /// </summary>
        public event Func<JoinCallAppHostedMediaOutcomeEvent, Task> OnJoinCallFailed;

        /// <summary>
        /// Event raised when bot receives call state change notification
        /// </summary>
        public event Func<CallStateChangeNotification, Task> OnCallStateChangeNotification;

        /// <summary>
        /// Event raised when bot receives roster update notification
        /// </summary>
        public event Func<RosterUpdateNotification, Task> OnRosterUpdateNotification;

        /// <summary>
        /// Event raised when the bot gets the outcome of JoinCallAppHostedMedia action. If the operation was successful the call is established
        /// </summary>
//        public event Func<JoinCallAppHostedMediaOutcomeEvent, Task> OnJoinCallAppHostedMediaCompleted;

        /// <summary>
        /// Event raised when bot needs to cleanup an existing call
        /// </summary>
        public event Func<Task> OnCallCleanup;

        /// <summary>
        /// Create a media session for this call.
        /// </summary>
        public virtual IRealTimeMediaSession CreateMediaSession(params NotificationType[] subscriptions)
        {
            return new RealTimeMediaSession(CorrelationId, subscriptions);
        }

        /// <summary>
        /// Create a media session for this call.
        /// </summary>
        public IReadOnlyMediaSession CurrentMediaSession { get; private set; }

        /// <summary>
        /// Instantiates the service with settings to handle a call
        /// </summary>
        /// <param name="parameters">The parameters for the RTM call service.</param>
        /// <param name="settings">The settings for the RTM call service.</param>
        public RealTimeMediaCallService(RealTimeMediaCallServiceParameters parameters, IRealTimeMediaCallServiceSettings settings)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (string.IsNullOrWhiteSpace(parameters.CallLegId) || string.IsNullOrWhiteSpace(parameters.CorrelationId))
            {
                throw new ArgumentNullException("call instance parameters");
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (settings.CallbackUrl == null || settings.NotificationUrl == null)
            {
                throw new ArgumentNullException("call global settings");
            }

            CallLegId = parameters.CallLegId;
            CorrelationId = parameters.CorrelationId;
            _callbackUrl = settings.CallbackUrl;
            _notificationUrl = settings.NotificationUrl;
            _placeCallUrl = settings.PlaceCallEndpointUrl;
            _botId = settings.BotId;
            _botSecret = settings.BotSecret;
            _timer = new Timer(CallExpiredTimerCallback, null, CallExpiredTimerInterval, Timeout.Infinite);
        }        

        /// <summary>
        /// Keeps track of receiving AnswerAppHostedMediaOutcome. If the answer does not come back, bot can start leaking sockets.
        /// </summary>
        /// <param name="state"></param>
        private void CallExpiredTimerCallback(object state)
        {
            Logger.LogInformation(
            $"RealTimeMediaCallService [{CallLegId}]: CallExpiredTimerCallback called.. cleaning up the call");

            Task.Run(async () =>
            {
                try
                {
                    await LocalCleanup().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        $"RealTimeMediaCallService [{CallLegId}]: Error in LocalCleanup {ex}");
                }
            });
        }

        /// <summary>
        /// Invokes notifications on the bot
        /// </summary>
        /// <param name="notification">Notification to be sent</param>
        /// <returns></returns>
        public Task ProcessNotificationResult(NotificationBase notification)
        {
            Logger.LogInformation(
                $"RealTimeMediaCallService [{CallLegId}]: Received the notification for {notification.Type} operation, callId: {notification.Id}");

            switch (notification.Type)
            {
                case NotificationType.CallStateChange:
                    return HandleCallStateChangeNotification(notification as CallStateChangeNotification);

                case NotificationType.RosterUpdate:
                    return HandleRosterUpdateNotification(notification as RosterUpdateNotification);
            }
            throw new BotCallingServiceException($"[{CallLegId}]: Unknown notification type {notification.Type}");
        }

        private async Task HandleCallStateChangeNotification(CallStateChangeNotification notification)
        {
            Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Received CallStateChangeNotification.. ");
            notification.Validate();

            var eventHandler = OnCallStateChangeNotification;
            if (eventHandler != null)
            {
                await eventHandler.Invoke(notification).ConfigureAwait(false);
            }
        }

        private async Task HandleRosterUpdateNotification(RosterUpdateNotification notification)
        {
            Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Received RosterUpdateNotification");
            notification.Validate();

            var eventHandler = OnRosterUpdateNotification;
            if (eventHandler != null)
            {
                await eventHandler.Invoke(notification).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Invokes handlers for callback on the bot
        /// </summary>
        /// <param name="conversationResult">ConversationResult that has the details of the callback</param>
        /// <returns></returns>
        public async Task<string> ProcessConversationResult(ConversationResult conversationResult)
        {
            conversationResult.Validate();
            var newWorkflowResult = await PassActionResultToHandler(conversationResult).ConfigureAwait(false);
            if (newWorkflowResult == null)
            {
                throw new BotCallingServiceException($"[{CallLegId}]: No workflow returned for AnswerAppHostedMediaOutcome");
            }

            bool expectEmptyActions = false;
            if(conversationResult.OperationOutcome.Type == RealTimeMediaValidOutcomes.AnswerAppHostedMediaOutcome && conversationResult.OperationOutcome.Outcome == Outcome.Success)
            {
                Uri link;
                if (conversationResult.Links.TryGetValue("subscriptions", out link))
                {
                    _subscriptionLink = link;
                    Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Caching subscription link {link}");
                }

                if (conversationResult.Links.TryGetValue("call", out link))
                {
                    _callLink = link;
                    Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Caching call link {link}");
                }
                expectEmptyActions = true;

                Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Disposing call expiry timer");
                _timer.Dispose();
            }
            else if (conversationResult.OperationOutcome.Type == RealTimeMediaValidOutcomes.JoinCallAppHostedMediaOutcome && conversationResult.OperationOutcome.Outcome == Outcome.Success)
            {
                Uri link;

                if (conversationResult.Links.TryGetValue("call", out link))
                {
                    _callLink = link;
                    Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Caching call link {link}");
                }
                expectEmptyActions = true;

                Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Disposing call expiry timer");
                _timer.Dispose();
            }

            newWorkflowResult.Validate(expectEmptyActions);
            return RealTimeMediaSerializer.SerializeToJson(newWorkflowResult);
        }

        private Task<Workflow> PassActionResultToHandler(ConversationResult receivedConversationResult)
        {
            Logger.LogInformation(
                $"RealTimeMediaCallService [{CallLegId}]: Received the outcome for {receivedConversationResult.OperationOutcome.Type} operation, callId: {receivedConversationResult.OperationOutcome.Id}");

            switch (receivedConversationResult.OperationOutcome.Type)
            {
                case RealTimeMediaValidOutcomes.AnswerAppHostedMediaOutcome:
                    return HandleAnswerAppHostedMediaOutcome(receivedConversationResult, receivedConversationResult.OperationOutcome as AnswerAppHostedMediaOutcome);

                case RealTimeMediaValidOutcomes.JoinCallAppHostedMediaOutcome:
                    return HandleJoinAppHostedMediaOutcome(receivedConversationResult, receivedConversationResult.OperationOutcome as JoinCallAppHostedMediaOutcome);

                case ValidOutcomes.WorkflowValidationOutcome:
                    return HandleWorkflowValidationOutcome(receivedConversationResult, receivedConversationResult.OperationOutcome as WorkflowValidationOutcome);
            }

            throw new BotCallingServiceException($"[{CallLegId}]: Unknown conversation result type {receivedConversationResult.OperationOutcome.Type}");
        }

        /// <summary>
        /// Invokes handler for incoming call
        /// </summary>
        /// <param name="conversation">Conversation corresponding to the incoming call</param>
        /// <returns>WorkFlow to be executed for the call</returns>
        public async Task<Workflow> HandleIncomingCall(Conversation conversation)
        {
            Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Received incoming call");
            var workflow = CreateInitialWorkflow();
            var incomingCall = new RealTimeMediaIncomingCallEvent(conversation, workflow);

            try
            {
                var eventHandler = OnIncomingCallReceived;
                if (eventHandler != null)
                    await eventHandler.Invoke(incomingCall).ConfigureAwait(false);
                else
                {
                    Logger.LogInformation(
                        $"RealTimeMediaCallService [{CallLegId}]: No handler specified for incoming call");
                    return null;
                }
            }
            catch (Exception e)
            {
                Logger.LogInformation(
                    $"RealTimeMediaCallService [{CallLegId}]: Invoking Incoming Call Failed {e}");

                throw;
            }

            this.CurrentMediaSession = incomingCall.MediaSession;
            return incomingCall.ResultingWorkflow;
        }

        private async Task<Workflow> HandleAnswerAppHostedMediaOutcome(ConversationResult conversationResult, AnswerAppHostedMediaOutcome answerAppHostedMediaOutcome)
        {
            try
            {
                Logger.LogInformation($"[{CorrelationId}] OnAnswerAppHostedMediaCompleted");
                var workflow = CreateInitialWorkflow();
                var outcomeEvent = new AnswerAppHostedMediaOutcomeEvent(conversationResult, workflow, answerAppHostedMediaOutcome);
                if (answerAppHostedMediaOutcome.Outcome == Outcome.Failure)
                {
                    Logger.LogWarning($"[{CorrelationId}] AnswerAppHostedMedia failed with reason: {answerAppHostedMediaOutcome.FailureReason}");
                    await InvokeHandlerIfSet(OnAnswerFailed, outcomeEvent).ConfigureAwait(false);
                }
                else
                {
                    workflow.NotificationSubscriptions = CurrentMediaSession.Subscriptions;
                    var eventHandler = OnAnswerSucceeded;
                    if (null != eventHandler)
                    {
                        // Optional event handler... user does not need to do anything.
                        await eventHandler.Invoke(outcomeEvent).ConfigureAwait(false);
                    }
                }
                return workflow;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[{CorrelationId}] threw {ex}");
                throw;
            }
        }

        protected virtual async Task PlaceCall(HttpContent content, string correlationId)
        {
            var placeCallEndpointUrl =  _placeCallUrl ?? _defaultPlaceCallEndpointUrl;

            //place the call
            try
            {
                Logger.LogInformation(
                    "RealTimeMediaBotService :Sending place call request");

                //TODO: add retries & logging
                using (var request = new HttpRequestMessage(HttpMethod.Post, placeCallEndpointUrl) { Content = content })
                {
                    var token = await GetBotToken(_botId, _botSecret).ConfigureAwait(false);

                    request.Headers.Add("X-Microsoft-Skype-Chain-ID", correlationId);
                    request.Headers.Add("X-Microsoft-Skype-Message-ID", Guid.NewGuid().ToString());
                    //TODO make this an http factory and inject it to the call service and bot service
                    var client = RealTimeMediaCallService.GetHttpClient();

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    var response = await client.SendAsync(request).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    Logger.LogInformation($"RealTimeMediaBotService [{correlationId}]: Response to join call: {response}");
                }
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    $"RealTimeMediaBotService [{correlationId}]: Received error while sending request to subscribe participant. Message: {exception}");
                throw;
            }
        }

        /// <summary>
        /// Method to obtain bot token from AAD
        /// </summary>
        /// <param name="botId"></param>
        /// <param name="botSecret"></param>
        /// <returns></returns>
        protected virtual async Task<string> GetBotToken(string botId, string botSecret)
        {
            if (null == botId)
            {
                throw new ArgumentNullException(nameof(botId));
            }

            if (null == botSecret)
            {
                throw new ArgumentNullException(nameof(botSecret));
            }

            var context = new AuthenticationContext(@"https://login.microsoftonline.com/common/oauth2/v2.0/token");
            var creds = new ClientCredential(botId, botSecret);
            var result = await context.AcquireTokenAsync(@"https://api.botframework.com", creds).ConfigureAwait(false);
            return result.AccessToken;
        }

        public virtual async Task JoinCall(JoinCallParameters joinCallParameters, IReadOnlyMediaSession session, string correlationId)
        {
            if (null == joinCallParameters)
            {
                throw new ArgumentNullException(nameof(joinCallParameters));
            }
            if (null == session)
            {
                throw new ArgumentNullException(nameof(session));
            }
            Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Received join call request");
            this.CurrentMediaSession = session;

            var joinCall = new JoinCallAppHostedMedia(joinCallParameters)
            {
                MediaConfiguration = session.GetMediaConfiguration(),
                OperationId = Guid.NewGuid().ToString()
            };

            var workflow = CreateInitialWorkflow();
            workflow.Actions = new ActionBase[] { joinCall };
            workflow.NotificationSubscriptions = session.Subscriptions;
            HttpContent content = new StringContent(RealTimeMediaSerializer.SerializeToJson(workflow), Encoding.UTF8, "application/json");

            await PlaceCall(content, correlationId).ConfigureAwait(false);

        }

        /// <summary>
        /// Invokes handler for join call
        /// </summary>
        /// <param name="joinCallParameters">Which call to join and how to join it.</param>
        /// <returns>WorkFlow to be executed for the call</returns>
        public async Task<Workflow> HandleJoinCall(JoinCallParameters joinCallParameters)
        {
            if (null == joinCallParameters)
            {
                throw new ArgumentNullException(nameof(joinCallParameters));
            }

            Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Received join call request");
            var joinCallEvent = new RealTimeMediaJoinCallEvent(joinCallParameters);

            try
            {
                var eventHandler = OnJoinCallRequested;
                if (eventHandler != null)
                    await eventHandler.Invoke(joinCallEvent).ConfigureAwait(false);
                else
                {
                    Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: No handler specified for join call");
                    return null;
                }
            }
            catch (Exception e)
            {
                Logger.LogInformation(
                    $"RealTimeMediaCallService [{CallLegId}]: Invoking Join Call Failed {e}");

                throw;
            }

            this.CurrentMediaSession = joinCallEvent.MediaSession;

            var joinCall = new JoinCallAppHostedMedia(joinCallParameters)
            {
                MediaConfiguration = joinCallEvent.MediaSession.GetMediaConfiguration(),
                OperationId = Guid.NewGuid().ToString()
            };

            var workflow = CreateInitialWorkflow();
            workflow.Actions = new ActionBase[] { joinCall };
            workflow.NotificationSubscriptions = joinCallEvent.MediaSession.Subscriptions;

            return workflow;
        }

        private async Task<Workflow> HandleJoinAppHostedMediaOutcome(ConversationResult conversationResult, JoinCallAppHostedMediaOutcome joinCallAppHostedMediaOutcome)
        {
            try
            {
                Logger.LogInformation($"[{CorrelationId}] OnJoinCallAppHostedMediaCompleted");
                var workflow = CreateInitialWorkflow();
                var outcomeEvent = new JoinCallAppHostedMediaOutcomeEvent(conversationResult, workflow, joinCallAppHostedMediaOutcome);
                if (joinCallAppHostedMediaOutcome.Outcome == Outcome.Failure)
                {
                    Logger.LogWarning($"[{CorrelationId}] JoinCallAppHostedMedia failed with reason: {joinCallAppHostedMediaOutcome.FailureReason}");
                    Logger.LogWarning($"[{CorrelationId}] JoinCallAppHostedMedia failed with completion: {joinCallAppHostedMediaOutcome.CompletionReason}");
                    await InvokeHandlerIfSet(OnJoinCallFailed, outcomeEvent).ConfigureAwait(false);
                }
                else
                {
                    workflow.NotificationSubscriptions = CurrentMediaSession.Subscriptions;
                    var eventHandler = OnJoinCallSucceeded;
                    if (null != eventHandler)
                    {
                        // Optional event handler... user does not need to do anything.
                        await eventHandler.Invoke(outcomeEvent).ConfigureAwait(false);
                    }
                }
                return workflow;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[{CorrelationId}] threw {ex}");
                throw;
            }
        }

        private Task<Workflow> HandleWorkflowValidationOutcome(
            ConversationResult conversationResult,
            WorkflowValidationOutcome workflowValidationOutcome)
        {
            var outcomeEvent = new RealTimeMediaWorkflowValidationOutcomeEvent(conversationResult, CreateInitialWorkflow(), workflowValidationOutcome);
            var eventHandler = OnWorkflowValidationFailed;
            return InvokeHandlerIfSet(eventHandler, outcomeEvent);
        }

        /// <summary>
        /// Clean up any local call
        /// </summary>
        public Task LocalCleanup()
        {
            var eventHandler = OnCallCleanup;            
            return InvokeHandlerIfSet(eventHandler, "Cleanup");
        }

        /// <summary>
        /// Subscribe to a video or video based screen sharing channel
        /// </summary>
        /// <param name="videoSubscription"></param>
        /// <returns></returns>
        public async Task Subscribe(VideoSubscription videoSubscription)
        {
            if (_subscriptionLink == null)
            {
                throw new InvalidOperationException($"[{CallLegId}]: No subscription link was present in the AnswerAppHostedMediaOutcome");
            }
            
            videoSubscription.Validate();
            HttpContent content = new StringContent(RealTimeMediaSerializer.SerializeToJson(videoSubscription), Encoding.UTF8, JSONConstants.ContentType);

            //Subscribe
            try
            {
                Logger.LogInformation(
                        $"RealTimeMediaCallService [{CallLegId}]: Sending subscribe request for " +
                        $"user: {videoSubscription.ParticipantIdentity}" +
                        $"subscriptionLink: {_subscriptionLink}");

                //TODO: add retries & logging
                using (var request = new HttpRequestMessage(HttpMethod.Put, _subscriptionLink) { Content = content })
                {
                    request.Headers.Add("X-Microsoft-Skype-Chain-ID", CorrelationId);
                    request.Headers.Add("X-Microsoft-Skype-Message-ID", Guid.NewGuid().ToString());

                    var client = GetHttpClient();
                    var response = await client.SendAsync(request).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Response to subscribe: {response}");
                }
            }
            catch (Exception exception)
            {
                Logger.LogError($"RealTimeMediaCallService [{CallLegId}]: Received error while sending request to subscribe participant. Message: {exception}");
                throw;
            }
        }

        internal static HttpClient GetHttpClient()
        {
            var clientHandler = new HttpClientHandler { AllowAutoRedirect = false };
            DelegatingHandler[] handlers = new DelegatingHandler[] { new RetryMessageHandler(), new LoggingMessageHandler() };
            HttpClient client = HttpClientFactory.Create(clientHandler, handlers);
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Microsoft-BotFramework-RealTimeMedia", assemblyVersion));
            return client;
        }

        /// <summary>
        /// Ends the call. Local cleanup will not be done
        /// </summary>
        /// <returns></returns>
        public async Task EndCall()
        {
            if (_callLink == null)
            {
                throw new InvalidOperationException($"[{CallLegId}]: No call link was present in the AnswerAppHostedMediaOutcome");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Delete, _callLink))
            {                
                request.Headers.Add("X-Microsoft-Skype-Chain-ID", CorrelationId);
                request.Headers.Add("X-Microsoft-Skype-Message-ID", Guid.NewGuid().ToString());

                var client = GetHttpClient();
                var response = await client.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Logger.LogInformation($"RealTimeMediaCallService [{CallLegId}]: Response to Delete: {response}");
            }
        }

        private async Task<Workflow> InvokeHandlerIfSet<T>(Func<T, Task> action, T outcomeEventBase) 
            where T : OutcomeEventBase
        {
            if (action == null)
            {
                throw new BotCallingServiceException(
                    $"[{CallLegId}]: No event handler set for {outcomeEventBase.ConversationResult.OperationOutcome.Type} outcome");
            }

            await action.Invoke(outcomeEventBase).ConfigureAwait(false);
            return outcomeEventBase.ResultingWorkflow;
        }

        private async Task InvokeHandlerIfSet(Func<Task> action, string type) 
        {
            if (action == null)
            {
                throw new BotCallingServiceException(
                    $"[{CallLegId}]: No event handler set for {type}");
            }

            await action.Invoke().ConfigureAwait(false);
        }

        private RealTimeMediaWorkflow CreateInitialWorkflow()
        {
            var workflow = new RealTimeMediaWorkflow();
            workflow.Links = GetCallbackLink();
            workflow.Actions = new List<ActionBase>();
            workflow.AppState = CallLegId;
            workflow.NotificationSubscriptions = new List<NotificationType>() { NotificationType.CallStateChange };
            return workflow;
        }

        private CallbackLink GetCallbackLink()
        {
            return new CallbackLink() { Callback = _callbackUrl, Notification = _notificationUrl };
        }
    }
}
