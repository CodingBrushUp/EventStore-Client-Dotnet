using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using Grpc.Core;
using static EventStore.Client.Streams.ReadResp;
using static EventStore.Client.Streams.ReadResp.ContentOneofCase;
#if !NET5_0_OR_GREATER
using Channel = System.Threading.Channels.Channel;
#endif

#nullable enable
namespace EventStore.Client {
	public partial class EventStoreClient {
		private ReadAllStreamResult ReadAllAsync(Direction direction, Position position, long maxCount,
			EventStoreClientOperationOptions operationOptions, bool resolveLinkTos = false,
			UserCredentials? userCredentials = null, CancellationToken cancellationToken = default) {
			if (maxCount <= 0) {
				throw new ArgumentOutOfRangeException(nameof(maxCount));
			}

			return new ReadAllStreamResult(SelectCallInvoker, new ReadReq {
				Options = new() {
					ReadDirection = direction switch {
						Direction.Backwards => ReadReq.Types.Options.Types.ReadDirection.Backwards,
						Direction.Forwards => ReadReq.Types.Options.Types.ReadDirection.Forwards,
						_ => throw InvalidOption(direction)
					},
					ResolveLinks = resolveLinkTos,
					All = new() {
						Position = new() {
							CommitPosition = position.CommitPosition,
							PreparePosition = position.PreparePosition
						}
					},
					Count = (ulong)maxCount,
					UuidOption = new() {Structured = new()},
					NoFilter = new(),
					ControlOption = new() {Compatibility = 1}
				}
			}, Settings, operationOptions, userCredentials, cancellationToken);
		}

		/// <summary>
		/// Asynchronously reads all events.
		/// </summary>
		/// <param name="direction">The <see cref="Direction"/> in which to read.</param>
		/// <param name="position">The <see cref="Position"/> to start reading from.</param>
		/// <param name="maxCount">The maximum count to read.</param>
		/// <param name="configureOperationOptions">An <see cref="Action{EventStoreClientOperationOptions}"/> to configure the operation's options.</param>
		/// <param name="resolveLinkTos">Whether to resolve LinkTo events automatically.</param>
		/// <param name="userCredentials">The optional <see cref="UserCredentials"/> to perform operation with.</param>
		/// <param name="cancellationToken">The optional <see cref="System.Threading.CancellationToken"/>.</param>
		/// <returns></returns>
		public ReadAllStreamResult ReadAllAsync(
			Direction direction,
			Position position,
			long maxCount = long.MaxValue,
			Action<EventStoreClientOperationOptions>? configureOperationOptions = null,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) {
			var operationOptions = Settings.OperationOptions.Clone();
			configureOperationOptions?.Invoke(operationOptions);

			return ReadAllAsync(direction, position, maxCount, operationOptions, resolveLinkTos, userCredentials,
				cancellationToken);
		}

		/// <summary>
		/// A class that represents the result of a read operation on the $all stream. You may either enumerate this instance directly or <see cref="Messages"/>. Do not enumerate more than once.
		/// </summary>
		public class ReadAllStreamResult : IAsyncEnumerable<ResolvedEvent> {
			private readonly Channel<StreamMessage> _channel;

			private int _messagesEnumerated;

			/// <summary>
			/// The last <see cref="Position"/> of the $all stream, if available.
			/// </summary>
			public Position? LastPosition { get; private set; }

			/// <summary>
			/// An <see cref="IAsyncEnumerable{StreamMessage}"/>. Do not enumerate more than once.
			/// </summary>
			public IAsyncEnumerable<StreamMessage> Messages {
				get {
					return GetMessages();

					async IAsyncEnumerable<StreamMessage> GetMessages() {
						if (Interlocked.Exchange(ref _messagesEnumerated, 1) == 1) {
							throw new InvalidOperationException("Messages may only be enumerated once.");
						}
						await foreach (var message in _channel.Reader.ReadAllAsync().ConfigureAwait(false)) {
							if (message is StreamMessage.LastAllStreamPosition(var position)) {
								LastPosition = position;
							}

							yield return message;
						}
					}
				}
			}

			internal ReadAllStreamResult(Func<CancellationToken, Task<CallInvoker>> selectCallInvoker, ReadReq request,
				EventStoreClientSettings settings, EventStoreClientOperationOptions operationOptions,
				UserCredentials? userCredentials, CancellationToken cancellationToken) {
				var callOptions = EventStoreCallOptions.Create(settings, operationOptions, userCredentials,
					cancellationToken);

				_channel = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(1) {
					SingleReader = true,
					SingleWriter = true,
					AllowSynchronousContinuations = true
				});

				_ = PumpMessages();

				async Task PumpMessages() {
					try {
						var callInvoker = await selectCallInvoker(cancellationToken).ConfigureAwait(false);
						var client = new Streams.Streams.StreamsClient(callInvoker);
						using var call = client.Read(request, callOptions);
						await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken)
							               .ConfigureAwait(false)) {
							await _channel.Writer.WriteAsync(response.ContentCase switch {
								StreamNotFound => StreamMessage.NotFound.Instance,
								Event => new StreamMessage.Event(ConvertToResolvedEvent(response.Event)),
								FirstStreamPosition => new StreamMessage.FirstStreamPosition(
									new StreamPosition(response.FirstStreamPosition)),
								LastStreamPosition => new StreamMessage.LastStreamPosition(
									new StreamPosition(response.LastStreamPosition)),
								LastAllStreamPosition => new StreamMessage.LastAllStreamPosition(
									new Position(response.LastAllStreamPosition.CommitPosition,
										response.LastAllStreamPosition.PreparePosition)),
								_ => StreamMessage.Unknown.Instance
							}, cancellationToken).ConfigureAwait(false);
						}

						_channel.Writer.Complete();
					} catch (Exception ex) {
						_channel.Writer.TryComplete(ex);
					}
				}
			}

			/// <inheritdoc />
			public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(
				CancellationToken cancellationToken = default) {
				await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
					if (message is not StreamMessage.Event e) {
						continue;
					}

					yield return e.ResolvedEvent;
				}
			}
		}

		private ReadStreamResult ReadStreamAsync(Direction direction, string streamName, StreamPosition revision,
			long maxCount, EventStoreClientOperationOptions operationOptions, bool resolveLinkTos,
			UserCredentials? userCredentials, CancellationToken cancellationToken) {
			if (maxCount <= 0) {
				throw new ArgumentOutOfRangeException(nameof(maxCount));
			}

			return new ReadStreamResult(SelectCallInvoker, new ReadReq {
				Options = new() {
					ReadDirection = direction switch {
						Direction.Backwards => ReadReq.Types.Options.Types.ReadDirection.Backwards,
						Direction.Forwards => ReadReq.Types.Options.Types.ReadDirection.Forwards,
						_ => throw InvalidOption(direction)
					},
					ResolveLinks = resolveLinkTos,
					Stream = ReadReq.Types.Options.Types.StreamOptions.FromStreamNameAndRevision(streamName,
						revision),
					Count = (ulong)maxCount,
					UuidOption = new() {Structured = new()},
					NoFilter = new(),
					ControlOption = new() {Compatibility = 1}
				}
			}, Settings, operationOptions, userCredentials, cancellationToken);
		}

		/// <summary>
		/// Asynchronously reads all the events from a stream.
		///
		/// The result could also be inspected as a means to avoid handling exceptions as the <see cref="ReadState"/> would indicate whether or not the stream is readable./>
		/// </summary>
		/// <param name="direction">The <see cref="Direction"/> in which to read.</param>
		/// <param name="streamName">The name of the stream to read.</param>
		/// <param name="revision">The <see cref="StreamRevision"/> to start reading from.</param>
		/// <param name="maxCount">The number of events to read from the stream.</param>
		/// <param name="configureOperationOptions">An <see cref="Action{EventStoreClientOperationOptions}"/> to configure the operation's options.</param>
		/// <param name="resolveLinkTos">Whether to resolve LinkTo events automatically.</param>
		/// <param name="userCredentials">The optional <see cref="UserCredentials"/> to perform operation with.</param>
		/// <param name="cancellationToken">The optional <see cref="System.Threading.CancellationToken"/>.</param>
		/// <returns></returns>
		public ReadStreamResult ReadStreamAsync(
			Direction direction,
			string streamName,
			StreamPosition revision,
			long maxCount = long.MaxValue,
			Action<EventStoreClientOperationOptions>? configureOperationOptions = null,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) {
			var operationOptions = Settings.OperationOptions.Clone();
			configureOperationOptions?.Invoke(operationOptions);

			return ReadStreamAsync(direction, streamName, revision, maxCount, operationOptions, resolveLinkTos,
				userCredentials, cancellationToken);
		}

		/// <summary>
		/// A class that represents the result of a read operation on a stream. You may either enumerate this instance directly or <see cref="Messages"/>. Do not enumerate more than once.
		/// </summary>
		public class ReadStreamResult : IAsyncEnumerable<ResolvedEvent> {
			private readonly Channel<StreamMessage> _channel;

			private int _messagesEnumerated;

			/// <summary>
			/// The name of the stream.
			/// </summary>
			public string StreamName { get; }

			/// <summary>
			/// The <see cref="StreamPosition"/> of the first message in this stream. Will only be filled once <see cref="Messages"/> has been enumerated. 
			/// </summary>
			public StreamPosition? FirstStreamPosition { get; private set; }

			/// <summary>
			/// The <see cref="StreamPosition"/> of the last message in this stream. Will only be filled once <see cref="Messages"/> has been enumerated. 
			/// </summary>
			public StreamPosition? LastStreamPosition { get; private set; }

			/// <summary>
			/// An <see cref="IAsyncEnumerable{StreamMessage}"/>. Do not enumerate more than once.
			/// </summary>
			public IAsyncEnumerable<StreamMessage> Messages {
				get {
					return GetMessages();

					async IAsyncEnumerable<StreamMessage> GetMessages() {
						if (Interlocked.Exchange(ref _messagesEnumerated, 1) == 1) {
							throw new InvalidOperationException("Messages may only be enumerated once.");
						}
						await foreach (var message in _channel.Reader.ReadAllAsync().ConfigureAwait(false)) {
							switch (message) {
								case StreamMessage.FirstStreamPosition(var streamPosition):
									FirstStreamPosition = streamPosition;
									break;
								case StreamMessage.LastStreamPosition(var lastStreamPosition):
									LastStreamPosition = lastStreamPosition;
									break;
								default:
									break;
							}

							yield return message;
						}
					}
				}
			}

			/// <summary>
			/// The <see cref="ReadState"/>.
			/// </summary>
			public Task<ReadState> ReadState { get; }

			internal ReadStreamResult(Func<CancellationToken, Task<CallInvoker>> selectCallInvoker, ReadReq request,
				EventStoreClientSettings settings, EventStoreClientOperationOptions operationOptions,
				UserCredentials? userCredentials, CancellationToken cancellationToken) {
				var callOptions = EventStoreCallOptions.Create(settings, operationOptions, userCredentials,
					cancellationToken);

				_channel = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(1) {
					SingleReader = true,
					SingleWriter = true,
					AllowSynchronousContinuations = true
				});

				StreamName = request.Options.Stream.StreamIdentifier;

				var tcs = new TaskCompletionSource<ReadState>();
#pragma warning disable CS0612
				ReadState = tcs.Task;
#pragma warning restore CS0612

				_ = PumpMessages();

				async Task PumpMessages() {
					var firstMessageRead = false;

					try {
						var callInvoker = await selectCallInvoker(cancellationToken).ConfigureAwait(false);
						var client = new Streams.Streams.StreamsClient(callInvoker);
						using var call = client.Read(request, callOptions);

						await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken)
							               .ConfigureAwait(false)) {
							if (!firstMessageRead) {
								firstMessageRead = true;

								if (response.ContentCase != StreamNotFound || request.Options.Stream == null) {
									await _channel.Writer.WriteAsync(StreamMessage.Ok.Instance, cancellationToken)
										.ConfigureAwait(false);
									tcs.SetResult(Client.ReadState.Ok);
								} else {
									tcs.SetResult(Client.ReadState.StreamNotFound);
								}
							}

							await _channel.Writer.WriteAsync(response.ContentCase switch {
								StreamNotFound => StreamMessage.NotFound.Instance,
								Event => new StreamMessage.Event(ConvertToResolvedEvent(response.Event)),
								ContentOneofCase.FirstStreamPosition => new StreamMessage.FirstStreamPosition(
									new StreamPosition(response.FirstStreamPosition)),
								ContentOneofCase.LastStreamPosition => new StreamMessage.LastStreamPosition(
									new StreamPosition(response.LastStreamPosition)),
								LastAllStreamPosition => new StreamMessage.LastAllStreamPosition(
									new Position(response.LastAllStreamPosition.CommitPosition,
										response.LastAllStreamPosition.PreparePosition)),
								_ => StreamMessage.Unknown.Instance
							}, cancellationToken).ConfigureAwait(false);
						}

						_channel.Writer.Complete();
					} catch (Exception ex) {
						tcs.TrySetException(ex);
						_channel.Writer.TryComplete(ex);
					}
				}
			}

			/// <inheritdoc />
			public async IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(
				CancellationToken cancellationToken = default) {
				await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
					if (message is StreamMessage.NotFound) {
						throw new StreamNotFoundException(StreamName);
					}

					if (message is not StreamMessage.Event e) {
						continue;
					}

					yield return e.ResolvedEvent;
				}
			}
		}

		private async IAsyncEnumerable<(SubscriptionConfirmation, Position?, ResolvedEvent)> ReadInternal(
			ReadReq request,
			EventStoreClientOperationOptions operationOptions,
			UserCredentials? userCredentials,
			[EnumeratorCancellation] CancellationToken cancellationToken) {
			if (request.Options.CountOptionCase == ReadReq.Types.Options.CountOptionOneofCase.Count &&
			    request.Options.Count <= 0) {
				throw new ArgumentOutOfRangeException("count");
			}

			if (request.Options.Filter == null) {
				request.Options.NoFilter = new Empty();
			}

			request.Options.UuidOption = new ReadReq.Types.Options.Types.UUIDOption {Structured = new Empty()};

			using var call = new Streams.Streams.StreamsClient(
				await SelectCallInvoker(cancellationToken).ConfigureAwait(false)).Read(request,
				EventStoreCallOptions.Create(Settings, operationOptions, userCredentials, cancellationToken));

			await foreach (var e in call.ResponseStream
				.ReadAllAsync(cancellationToken)
				.Select(ConvertToItem)
				.WithCancellation(cancellationToken)
				.ConfigureAwait(false)) {
				if (e.HasValue) {
					yield return e.Value;
				}
			}
		}

		private static (SubscriptionConfirmation, Position?, ResolvedEvent)? ConvertToItem(ReadResp response) =>
			response.ContentCase switch {
				Confirmation => (
					new SubscriptionConfirmation(response.Confirmation.SubscriptionId), null, default),
				Event => (SubscriptionConfirmation.None,
					null,
					ConvertToResolvedEvent(response.Event)),
				Checkpoint => (SubscriptionConfirmation.None,
					new Position(response.Checkpoint.CommitPosition, response.Checkpoint.PreparePosition),
					default),
				_ => null
			};

		private static ResolvedEvent ConvertToResolvedEvent(ReadResp.Types.ReadEvent readEvent) =>
			new ResolvedEvent(
				ConvertToEventRecord(readEvent.Event)!,
				ConvertToEventRecord(readEvent.Link),
				readEvent.PositionCase switch {
					ReadResp.Types.ReadEvent.PositionOneofCase.CommitPosition => readEvent.CommitPosition,
					_ => null
				});

		private static EventRecord? ConvertToEventRecord(ReadResp.Types.ReadEvent.Types.RecordedEvent? e) =>
			e == null
				? null
				: new EventRecord(
					e.StreamIdentifier,
					Uuid.FromDto(e.Id),
					new StreamPosition(e.StreamRevision),
					new Position(e.CommitPosition, e.PreparePosition),
					e.Metadata,
					e.Data.ToByteArray(),
					e.CustomMetadata.ToByteArray());
	}
}
