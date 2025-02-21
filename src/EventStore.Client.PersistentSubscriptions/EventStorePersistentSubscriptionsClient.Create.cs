using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.PersistentSubscriptions;

#nullable enable
namespace EventStore.Client {
	partial class EventStorePersistentSubscriptionsClient {
		private static readonly IDictionary<string, CreateReq.Types.ConsumerStrategy> NamedConsumerStrategyToCreateProto
			= new Dictionary<string, CreateReq.Types.ConsumerStrategy> {
				[SystemConsumerStrategies.DispatchToSingle] = CreateReq.Types.ConsumerStrategy.DispatchToSingle,
				[SystemConsumerStrategies.RoundRobin] = CreateReq.Types.ConsumerStrategy.RoundRobin,
				[SystemConsumerStrategies.Pinned] = CreateReq.Types.ConsumerStrategy.Pinned,
			};

		private static CreateReq.Types.StreamOptions StreamOptionsForCreateProto(string streamName, StreamPosition position) {
			if (position == StreamPosition.Start) {
				return new CreateReq.Types.StreamOptions {
					StreamIdentifier = streamName,
					Start = new Empty()
				};
			}

			if (position == StreamPosition.End) {
				return new CreateReq.Types.StreamOptions {
					StreamIdentifier = streamName,
					End = new Empty()
				};
			}

			return new CreateReq.Types.StreamOptions {
				StreamIdentifier = streamName,
				Revision = position.ToUInt64()
			};
		}

		private static CreateReq.Types.AllOptions AllOptionsForCreateProto(Position position, IEventFilter? filter) {
			var allFilter = GetFilterOptions(filter);
			CreateReq.Types.AllOptions allOptions;
			if (position == Position.Start) {
				allOptions = new CreateReq.Types.AllOptions {
					Start = new Empty(),
				};
			}
			else if (position == Position.End) {
				allOptions = new CreateReq.Types.AllOptions {
					End = new Empty()
				};
			} else {
				allOptions = new CreateReq.Types.AllOptions {
					Position = new CreateReq.Types.Position {
						CommitPosition = position.CommitPosition,
						PreparePosition = position.PreparePosition
					}
				};
			}

			if (allFilter is null) {
				allOptions.NoFilter = new Empty();
			} else {
				allOptions.Filter = allFilter;
			}

			return allOptions;
		}

		private static CreateReq.Types.AllOptions.Types.FilterOptions? GetFilterOptions(IEventFilter? filter) {
			if (filter == null) {
				return null;
			}

			var options = filter switch {
				StreamFilter _ => new CreateReq.Types.AllOptions.Types.FilterOptions {
					StreamIdentifier = (filter.Prefixes, filter.Regex) switch {
						(PrefixFilterExpression[] _, RegularFilterExpression _)
							when (filter.Prefixes?.Length ?? 0) == 0 &&
							     filter.Regex != RegularFilterExpression.None =>
							new CreateReq.Types.AllOptions.Types.FilterOptions.Types.Expression
								{Regex = filter.Regex},
						(PrefixFilterExpression[] _, RegularFilterExpression _)
							when (filter.Prefixes?.Length ?? 0) != 0 &&
							     filter.Regex == RegularFilterExpression.None =>
							new CreateReq.Types.AllOptions.Types.FilterOptions.Types.Expression {
								Prefix = {Array.ConvertAll(filter.Prefixes!, e => e.ToString())}
							},
						_ => throw new InvalidOperationException()
					}
				},
				EventTypeFilter _ => new CreateReq.Types.AllOptions.Types.FilterOptions {
					EventType = (filter.Prefixes, filter.Regex) switch {
						(PrefixFilterExpression[] _, RegularFilterExpression _)
							when (filter.Prefixes?.Length ?? 0) == 0 &&
							     filter.Regex != RegularFilterExpression.None =>
							new CreateReq.Types.AllOptions.Types.FilterOptions.Types.Expression
								{Regex = filter.Regex},
						(PrefixFilterExpression[] _, RegularFilterExpression _)
							when (filter.Prefixes?.Length ?? 0) != 0 &&
							     filter.Regex == RegularFilterExpression.None =>
							new CreateReq.Types.AllOptions.Types.FilterOptions.Types.Expression {
								Prefix = {Array.ConvertAll(filter.Prefixes!, e => e.ToString())}
							},
						_ => throw new InvalidOperationException()
					}
				},
				_ => throw new InvalidOperationException()
			};

			if (filter.MaxSearchWindow.HasValue) {
				options.Max = filter.MaxSearchWindow.Value;
			} else {
				options.Count = new Empty();
			}

			return options;
		}


		/// <summary>
		/// Creates a persistent subscription.
		/// </summary>
		/// <param name="streamName"></param>
		/// <param name="groupName"></param>
		/// <param name="settings"></param>
		/// <param name="userCredentials"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		/// <exception cref="ArgumentNullException"></exception>
		public async Task CreateAsync(string streamName, string groupName,
			PersistentSubscriptionSettings settings, UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) =>
			await CreateInternalAsync(
					streamName: streamName,
					groupName: groupName,
					eventFilter: null,
					settings: settings,
					userCredentials: userCredentials,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);

		private async Task CreateInternalAsync(string streamName, string groupName, IEventFilter? eventFilter,
			PersistentSubscriptionSettings settings, UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) {
			if (streamName == null) {
				throw new ArgumentNullException(nameof(streamName));
			}

			if (groupName == null) {
				throw new ArgumentNullException(nameof(groupName));
			}

			if (settings == null) {
				throw new ArgumentNullException(nameof(settings));
			}

			if (streamName != SystemStreams.AllStream && settings.StartFrom != null && !(settings.StartFrom is StreamPosition)) {
				throw new ArgumentException($"{nameof(settings.StartFrom)} must be of type '{nameof(StreamPosition)}' when subscribing to a stream");
			}

			if (streamName == SystemStreams.AllStream && settings.StartFrom != null && !(settings.StartFrom is Position)) {
				throw new ArgumentException($"{nameof(settings.StartFrom)} must be of type '{nameof(Position)}' when subscribing to {SystemStreams.AllStream}");
			}

			if (eventFilter != null && streamName != SystemStreams.AllStream) {
				throw new ArgumentException($"Filters are only supported when subscribing to {SystemStreams.AllStream}");
			}

			await new PersistentSubscriptions.PersistentSubscriptions.PersistentSubscriptionsClient(
				await SelectCallInvoker(cancellationToken).ConfigureAwait(false)).CreateAsync(new CreateReq {
				Options = new CreateReq.Types.Options {
					Stream = streamName != SystemStreams.AllStream ?
						StreamOptionsForCreateProto(streamName, (StreamPosition)(settings.StartFrom ?? StreamPosition.End)) : null,
					All = streamName == SystemStreams.AllStream ?
						AllOptionsForCreateProto((Position)(settings.StartFrom ?? Position.End), eventFilter) : null,
					#pragma warning disable 612
					StreamIdentifier = streamName != SystemStreams.AllStream ? streamName : string.Empty, /*for backwards compatibility*/
					#pragma warning restore 612
					GroupName = groupName,
					Settings = new CreateReq.Types.Settings {
						#pragma warning disable 612
						Revision = streamName != SystemStreams.AllStream ? ((StreamPosition)(settings.StartFrom ?? StreamPosition.End)).ToUInt64() : default, /*for backwards compatibility*/
						#pragma warning restore 612
						CheckpointAfterMs = (int)settings.CheckPointAfter.TotalMilliseconds,
						ExtraStatistics = settings.ExtraStatistics,
						MessageTimeoutMs = (int)settings.MessageTimeout.TotalMilliseconds,
						ResolveLinks = settings.ResolveLinkTos,
						HistoryBufferSize = settings.HistoryBufferSize,
						LiveBufferSize = settings.LiveBufferSize,
						MaxCheckpointCount = settings.MaxCheckPointCount,
						MaxRetryCount = settings.MaxRetryCount,
						MaxSubscriberCount = settings.MaxSubscriberCount,
						MinCheckpointCount = settings.MinCheckPointCount,
						NamedConsumerStrategy = NamedConsumerStrategyToCreateProto[settings.NamedConsumerStrategy],
						ReadBatchSize = settings.ReadBatchSize
					}
				}
			}, EventStoreCallOptions.Create(Settings, Settings.OperationOptions, userCredentials, cancellationToken));
		}

		/// <summary>
		/// Creates a filtered persistent subscription to $all.
		/// </summary>
		/// <param name="groupName"></param>
		/// <param name="eventFilter"></param>
		/// <param name="settings"></param>
		/// <param name="userCredentials"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task CreateToAllAsync(string groupName, IEventFilter eventFilter,
			PersistentSubscriptionSettings settings, UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) =>
			await CreateInternalAsync(
				streamName: SystemStreams.AllStream,
				groupName: groupName,
				eventFilter: eventFilter,
				settings: settings,
				userCredentials: userCredentials,
				cancellationToken: cancellationToken)
				.ConfigureAwait(false);

		/// <summary>
		/// Creates a persistent subscription to $all.
		/// </summary>
		/// <param name="groupName"></param>
		/// <param name="settings"></param>
		/// <param name="userCredentials"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task CreateToAllAsync(string groupName,
			PersistentSubscriptionSettings settings, UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) =>
			await CreateInternalAsync(
					streamName: SystemStreams.AllStream,
					groupName: groupName,
					eventFilter: null,
					settings: settings,
					userCredentials: userCredentials,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
	}
}
