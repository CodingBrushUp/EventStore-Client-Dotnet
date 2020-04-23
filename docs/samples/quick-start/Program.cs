﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client;

namespace quick_start {
	class Program {
		static async Task Main(string[] args) {
			
			//creating-connection
			var settings = new EventStoreClientSettings {
				ConnectivitySettings = {
					Address = new Uri("https://localhost:2113")
				}
			};

			var connection = new EventStoreClient(settings);
			//creating-connection

			//append-to-stream
			var eventData = new EventData(
				Uuid.NewUuid(),
				"some-event",
				Encoding.UTF8.GetBytes("{\"id\": \"1\" \"value\": \"some value\"}")
			);

			await connection.AppendToStreamAsync(
				"some-stream",
				AnyStreamRevision.Any, 
				new List<EventData> {
					eventData
				});
			//append-to-stream
			
			//read-stream
			var events = connection.ReadStreamAsync(Direction.Forwards, "some-stream", StreamRevision.Start, 1);

			await foreach (var @event in events) {
				Console.WriteLine(Encoding.UTF8.GetString(@event.Event.Data.Span));
			}
			//read-stream
		}
	}
}
