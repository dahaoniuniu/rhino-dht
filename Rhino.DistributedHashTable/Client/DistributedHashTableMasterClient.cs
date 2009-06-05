using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Google.ProtocolBuffers;
using Rhino.DistributedHashTable.Exceptions;
using Rhino.DistributedHashTable.Internal;
using Rhino.DistributedHashTable.Protocol;
using NodeEndpoint = Rhino.DistributedHashTable.Internal.NodeEndpoint;
using Segment = Rhino.DistributedHashTable.Internal.Segment;
using Rhino.DistributedHashTable.Util;

namespace Rhino.DistributedHashTable.Client
{
	public class DistributedHashTableMasterClient : IDistributedHashTableMaster
	{
		private readonly Uri uri;

		public DistributedHashTableMasterClient(Uri uri)
		{
			this.uri = uri;
		}

		private T Execute<T>(Func<MessageStreamWriter<MasterMessageUnion>, Stream, T> func)
		{
			using (var client = new TcpClient(uri.Host, uri.Port))
			using (var stream = client.GetStream())
			{
				var writer = new MessageStreamWriter<MasterMessageUnion>(stream);
				return func(writer, stream);
			}
		}

		private void Execute(Action<MessageStreamWriter<MasterMessageUnion>, Stream> func)
		{
			Execute<object>((writer,
							 stream) =>
			{
				func(writer, stream);
				return null;
			});
		}

		public Segment[] Join(NodeEndpoint endpoint)
		{
			return Execute((writer, stream) =>
			{
				writer.Write(new MasterMessageUnion.Builder
				{
					Type = MasterMessageType.JoinRequest,
					JoinRequest = new JoinRequestMessage.Builder
					{
						EndpointJoining = new Protocol.NodeEndpoint.Builder
						{
							Async = endpoint.Async.ToString(),
							Sync = endpoint.Sync.ToString()
						}.Build()
					}.Build()
				}.Build());
				writer.Flush();
				stream.Flush();

				var union = ReadReply(MasterMessageType.JoinResult, stream);

				var response = union.JoinResponse;

				return response.SegmentsList.Select(x => ConvertSegment(x)).ToArray();
			});
		}

		private MasterMessageUnion ReadReply(MasterMessageType responses, Stream stream)
		{
			var iterator = MessageStreamIterator<MasterMessageUnion>.FromStreamProvider(() => new UndisposableStream(stream));
			var union = iterator.First();

			if (union.Type == MasterMessageType.MasterErrorResult)
				throw new RemoteNodeException(union.Exception.Message);
			if (union.Type != responses)
				throw new UnexpectedReplyException("Got reply " + union.Type + " but expected " + responses);

			return union;
		}

		public void CaughtUp(NodeEndpoint endpoint,
							 params int[] caughtUpSegments)
		{
			Execute((writer,
							stream) =>
			{
				writer.Write(new MasterMessageUnion.Builder
				{
					Type = MasterMessageType.CaughtUpRequest,
					CaughtUp = new CaughtUpRequestMessage.Builder
					{
						CaughtUpSegmentsList = { caughtUpSegments },
						Endpoint = new Protocol.NodeEndpoint.Builder
						{
							Async = endpoint.Async.ToString(),
							Sync = endpoint.Sync.ToString()
						}.Build()
					}.Build()
				}.Build());
				writer.Flush();
				stream.Flush();

				ReadReply(MasterMessageType.CaughtUpResponse, stream);
			});
		}

		public Topology GetTopology()
		{
			return Execute((writer, stream) =>
			{
				writer.Write(new MasterMessageUnion.Builder
				{
					Type = MasterMessageType.GetTopologyRequest,
				}.Build());
				writer.Flush();
				stream.Flush();

				var union = ReadReply(MasterMessageType.GetTopologyResult, stream);

				var topology = union.Topology;
				var segments = topology.SegmentsList.Select(x => ConvertSegment(x));
				return new Topology(segments.ToArray(), new Guid(topology.Version.ToByteArray()))
				{
					Timestamp = DateTime.FromOADate(topology.TimestampAsDouble)
				};
			});
		}

		private static Segment ConvertSegment(Protocol.Segment x)
		{
			return new Segment
			{
				Version = new Guid(x.Version.ToByteArray()),
				AssignedEndpoint = x.AssignedEndpoint != Protocol.NodeEndpoint.DefaultInstance
									? new NodeEndpoint
									{
										Async = new Uri(x.AssignedEndpoint.Async),
										Sync = new Uri(x.AssignedEndpoint.Sync)
									}
									: null,
				InProcessOfMovingToEndpoint = x.InProcessOfMovingToEndpoint != Protocol.NodeEndpoint.DefaultInstance
												? new NodeEndpoint
												{
													Async = new Uri(x.InProcessOfMovingToEndpoint.Async),
													Sync = new Uri(x.InProcessOfMovingToEndpoint.Sync)
												}
												: null,
				Index = x.Index,
				Backups = x.BackupsList.Select(b => new NodeEndpoint
				{
					Async = new Uri(b.Async),
					Sync = new Uri(b.Sync)
				}).ToSet(),
			};
		}

		public void GaveUp(NodeEndpoint endpoint,
						   params int[] rangesGivingUpOn)
		{
			Execute((writer,
							stream) =>
			{
				writer.Write(new MasterMessageUnion.Builder
				{
					Type = MasterMessageType.GaveUpRequest,
					GaveUp = new GaveUpRequestMessage.Builder
					{
						GaveUpSegmentsList = { rangesGivingUpOn },
						Endpoint = new Protocol.NodeEndpoint.Builder
						{
							Async = endpoint.Async.ToString(),
							Sync = endpoint.Sync.ToString()
						}.Build()
					}.Build()
				}.Build());
				writer.Flush();
				stream.Flush();

				ReadReply(MasterMessageType.GaveUpResponse, stream);
			});
		}
	}
}