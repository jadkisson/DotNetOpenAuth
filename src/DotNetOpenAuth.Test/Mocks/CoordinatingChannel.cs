﻿//-----------------------------------------------------------------------
// <copyright file="CoordinatingChannel.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.Test.Mocks {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using DotNetOpenAuth.Messaging;

	internal class CoordinatingChannel : Channel {
		private Channel wrappedChannel;
		private EventWaitHandle incomingMessageSignal = new AutoResetEvent(false);
		private IProtocolMessage incomingMessage;
		private Response incomingRawResponse;
		private Action<IProtocolMessage> incomingMessageFilter;
		private Action<IProtocolMessage> outgoingMessageFilter;

		internal CoordinatingChannel(Channel wrappedChannel, Action<IProtocolMessage> incomingMessageFilter, Action<IProtocolMessage> outgoingMessageFilter)
			: base(GetMessageFactory(wrappedChannel), wrappedChannel.BindingElements.ToArray()) {
			ErrorUtilities.VerifyArgumentNotNull(wrappedChannel, "wrappedChannel");

			this.wrappedChannel = wrappedChannel;
			this.incomingMessageFilter = incomingMessageFilter;
			this.outgoingMessageFilter = outgoingMessageFilter;
		}

		/// <summary>
		/// Gets or sets the coordinating channel used by the other party.
		/// </summary>
		internal CoordinatingChannel RemoteChannel { get; set; }

		protected internal override HttpRequestInfo GetRequestFromContext() {
			return new HttpRequestInfo((IDirectedProtocolMessage)this.AwaitIncomingMessage());
		}

		protected override IProtocolMessage RequestInternal(IDirectedProtocolMessage request) {
			this.ProcessMessageFilter(request, true);
			HttpRequestInfo requestInfo = this.SpoofHttpMethod(request);
			// Drop the outgoing message in the other channel's in-slot and let them know it's there.
			this.RemoteChannel.incomingMessage = requestInfo.Message;
			this.RemoteChannel.incomingMessageSignal.Set();
			// Now wait for a response...
			IProtocolMessage response = this.AwaitIncomingMessage();
			this.ProcessMessageFilter(response, false);
			return response;
		}

		protected override Response SendDirectMessageResponse(IProtocolMessage response) {
			this.ProcessMessageFilter(response, true);
			this.RemoteChannel.incomingMessage = CloneSerializedParts(response, null);
			this.RemoteChannel.incomingMessageSignal.Set();
			return null;
		}

		protected override Response SendIndirectMessage(IDirectedProtocolMessage message) {
			this.ProcessMessageFilter(message, true);
			// In this mock transport, direct and indirect messages are the same.
			return this.SendDirectMessageResponse(message);
		}

		protected override IDirectedProtocolMessage ReadFromRequestInternal(HttpRequestInfo request) {
			this.ProcessMessageFilter(request.Message, false);
			return request.Message;
		}

		protected override IDictionary<string, string> ReadFromResponseInternal(Response response) {
			Channel_Accessor accessor = Channel_Accessor.AttachShadow(this.wrappedChannel);
			return accessor.ReadFromResponseInternal(response);
		}

		/// <summary>
		/// Spoof HTTP request information for signing/verification purposes.
		/// </summary>
		/// <param name="message">The message to add a pretend HTTP method to.</param>
		/// <returns>A spoofed HttpRequestInfo that wraps the new message.</returns>
		protected virtual HttpRequestInfo SpoofHttpMethod(IDirectedProtocolMessage message) {
			HttpRequestInfo requestInfo = new HttpRequestInfo(message);

			requestInfo.Message = this.CloneSerializedParts(message, requestInfo);

			return requestInfo;
		}

		protected virtual T CloneSerializedParts<T>(T message, HttpRequestInfo requestInfo) where T : class, IProtocolMessage {
			ErrorUtilities.VerifyArgumentNotNull(message, "message");

			IProtocolMessage clonedMessage;
			MessageSerializer serializer = MessageSerializer.Get(message.GetType());
			var fields = serializer.Serialize(message);

			MessageReceivingEndpoint recipient = null;
			var directedMessage = message as IDirectedProtocolMessage;
			var directResponse = message as IDirectResponseProtocolMessage;
			if (directedMessage != null && directedMessage.IsRequest()) {
				if (directedMessage.Recipient != null) {
					recipient = new MessageReceivingEndpoint(directedMessage.Recipient, directedMessage.HttpMethods);
				}

				clonedMessage = this.RemoteChannel.MessageFactory.GetNewRequestMessage(recipient, fields);
			} else if (directResponse != null && directResponse.IsDirectResponse()) {
				clonedMessage = this.RemoteChannel.MessageFactory.GetNewResponseMessage(directResponse.OriginatingRequest, fields);
			} else {
				throw new InvalidOperationException("Totally expected a message to implement one of the two derived interface types.");
			}

			// Fill the cloned message with data.
			serializer.Deserialize(fields, clonedMessage);

			return (T)clonedMessage;
		}

		private static IMessageFactory GetMessageFactory(Channel channel) {
			ErrorUtilities.VerifyArgumentNotNull(channel, "channel");

			Channel_Accessor accessor = Channel_Accessor.AttachShadow(channel);
			return accessor.MessageFactory;
		}

		private IProtocolMessage AwaitIncomingMessage() {
			this.incomingMessageSignal.WaitOne();
			IProtocolMessage response = this.incomingMessage;
			this.incomingMessage = null;
			return response;
		}

		private void ProcessMessageFilter(IProtocolMessage message, bool outgoing) {
			if (outgoing) {
				if (this.outgoingMessageFilter != null) {
					this.outgoingMessageFilter(message);
				}
			} else {
				if (this.incomingMessageFilter != null) {
					this.incomingMessageFilter(message);
				}
			}
		}
	}
}