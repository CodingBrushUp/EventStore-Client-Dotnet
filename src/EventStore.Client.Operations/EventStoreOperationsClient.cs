﻿using System;
using System.Collections.Generic;
using System.Linq;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#nullable enable
namespace EventStore.Client {
	/// <summary>
	/// The client used to performance maintenance and other administrative tasks on the EventStoreDB.
	/// </summary>
	public partial class EventStoreOperationsClient : EventStoreClientBase {
		private static readonly IDictionary<string, Func<RpcException, Exception>> ExceptionMap =
			new Dictionary<string, Func<RpcException, Exception>> {
				[Constants.Exceptions.ScavengeNotFound] = ex => new ScavengeNotFoundException(ex.Trailers
					.FirstOrDefault(x => x.Key == Constants.Exceptions.ScavengeId)?.Value)
			};

		private readonly Operations.Operations.OperationsClient _client;
		private readonly ILogger _log;

		/// <summary>
		/// Constructs a new <see cref="EventStoreOperationsClient"/>. This method is not intended to be called directly in your code.
		/// </summary>
		/// <param name="options"></param>
		public EventStoreOperationsClient(IOptions<EventStoreClientSettings> options) : this(options.Value) {
		}

		/// <summary>
		/// Constructs a new <see cref="EventStoreOperationsClient"/>.
		/// </summary>
		/// <param name="settings"></param>
		public EventStoreOperationsClient(EventStoreClientSettings? settings = null) : base(settings, ExceptionMap) {
			_client = new Operations.Operations.OperationsClient(CallInvoker);
			_log = Settings.LoggerFactory?.CreateLogger<EventStoreOperationsClient>() ??
			       new NullLogger<EventStoreOperationsClient>();
		}
	}
}
