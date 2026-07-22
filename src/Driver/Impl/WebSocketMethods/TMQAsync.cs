using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public class TMQConnectionAsync : BaseConnectionAsync
    {
        private const int MaximumCollectionItems = 65536;
        private const int MaximumTopicLength = 4096;
        private const int MaximumInstanceLength = 4096;

        public TMQConnectionAsync(TMQOptions options, TimeSpan connectTimeout = default(TimeSpan),
            TimeSpan readTimeout = default(TimeSpan), TimeSpan writeTimeout = default(TimeSpan))
            : base(TMQConnection.GetUrl(options), connectTimeout, readTimeout, writeTimeout,
                IsCompressionEnabled(options))
        {
        }

        private static bool IsCompressionEnabled(TMQOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return string.Equals(options.TDEnableCompression, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetUrl(TMQOptions options)
        {
            return TMQConnection.GetUrl(options);
        }

        public Task ConnectAsync()
        {
            return ConnectAsync(CancellationToken.None);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ClientConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the connection failure.
                }

                throw;
            }
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(List<string> topics, TMQOptions options)
        {
            return SubscribeAsync(topics, options, false, CancellationToken.None);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(List<string> topics, TMQOptions options,
            CancellationToken cancellationToken)
        {
            return SubscribeAsync(topics, options, false, cancellationToken);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(List<string> topics, TMQOptions options, bool listInstances)
        {
            return SubscribeAsync(topics, options, listInstances, CancellationToken.None);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(List<string> topics, TMQOptions options, bool listInstances,
            CancellationToken cancellationToken)
        {
            return SubscribeAsync(_GetReqId(), topics, options, listInstances, cancellationToken);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(ulong reqId, List<string> topics, TMQOptions options)
        {
            return SubscribeAsync(reqId, topics, options, false, CancellationToken.None);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(ulong reqId, List<string> topics, TMQOptions options,
            bool listInstances)
        {
            return SubscribeAsync(reqId, topics, options, listInstances, CancellationToken.None);
        }

        public Task<WSTMQSubscribeResp> SubscribeAsync(ulong reqId, List<string> topics, TMQOptions options,
            CancellationToken cancellationToken)
        {
            return SubscribeAsync(reqId, topics, options, false, cancellationToken);
        }

        public async Task<WSTMQSubscribeResp> SubscribeAsync(ulong reqId, List<string> topics, TMQOptions options,
            bool listInstances, CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopics(topics);
            if (options == null) throw new ArgumentNullException(nameof(options));

            var response = await SendJsonBackJsonAsync<WSTMQSubscribeReq, WSTMQSubscribeResp>(
                WSTMQAction.TMQSubscribe,
                new WSTMQSubscribeReq
                {
                    ReqId = reqId,
                    User = options.TDConnectUser,
                    Password = options.TDConnectPasswd,
                    Db = options.TDDatabase,
                    GroupId = options.GroupId,
                    ClientId = options.ClientId,
                    OffsetRest = options.AutoOffsetReset,
                    Topics = new List<string>(topics),
                    AutoCommit = "false",
                    AutoCommitIntervalMs = options.AutoCommitIntervalMs,
                    WithTableName = options.MsgWithTableName,
                    SessionTimeoutMs = options.SessionTimeoutMs,
                    MaxPollIntervalMs = options.MaxPollIntervalMs,
                    Config = options.GetOtherProperties(),
                    ListInstances = listInstances ? true : (bool?)null
                }, reqId, cancellationToken).ConfigureAwait(false);
            ValidateInstances(response.ListInstances);
            return response;
        }

        public Task<WSTMQPollResp> PollAsync(long blockingTime)
        {
            return PollAsync(_GetReqId(), blockingTime, 0UL, CancellationToken.None);
        }

        public Task<WSTMQPollResp> PollAsync(long blockingTime, CancellationToken cancellationToken)
        {
            return PollAsync(_GetReqId(), blockingTime, 0UL, cancellationToken);
        }

        public Task<WSTMQPollResp> PollAsync(long blockingTime, ulong lastMessageId)
        {
            return PollAsync(_GetReqId(), blockingTime, lastMessageId, CancellationToken.None);
        }

        public Task<WSTMQPollResp> PollAsync(ulong reqId, long blockingTime)
        {
            return PollAsync(reqId, blockingTime, 0UL, CancellationToken.None);
        }

        public Task<WSTMQPollResp> PollAsync(ulong reqId, long blockingTime, CancellationToken cancellationToken)
        {
            return PollAsync(reqId, blockingTime, 0UL, cancellationToken);
        }

        public Task<WSTMQPollResp> PollAsync(ulong reqId, long blockingTime, ulong lastMessageId)
        {
            return PollAsync(reqId, blockingTime, lastMessageId, CancellationToken.None);
        }

        public async Task<WSTMQPollResp> PollAsync(ulong reqId, long blockingTime, ulong lastMessageId,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            if (blockingTime < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockingTime),
                    "blockingTime cannot be negative.");
            }

            var response = await SendJsonBackJsonAsync<WSTMQPollReq, WSTMQPollResp>(WSTMQAction.TMQPoll,
                new WSTMQPollReq
                {
                    ReqId = reqId,
                    BlockingTime = blockingTime,
                    MessageId = lastMessageId
                }, reqId, cancellationToken).ConfigureAwait(false);
            ValidatePollResponse(response);
            return response;
        }

        public Task<byte[]> FetchBlockAsync(ulong reqId, ulong messageId)
        {
            return FetchBlockAsync(reqId, messageId, CancellationToken.None);
        }

        public Task<byte[]> FetchBlockAsync(ulong reqId, ulong messageId, CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateMessageId(messageId);
            return SendJsonBackBytesAsync(WSTMQAction.TMQFetchBlock, new WSTMQFetchBlockReq
            {
                ReqId = reqId,
                MessageId = messageId
            }, reqId, cancellationToken);
        }

        public Task<byte[]> FetchRawBlockAsync(ulong messageId)
        {
            return FetchRawBlockAsync(messageId, CancellationToken.None);
        }

        public Task<byte[]> FetchRawBlockAsync(ulong messageId, CancellationToken cancellationToken)
        {
            return FetchRawBlockAsync(_GetReqId(), messageId, cancellationToken);
        }

        public Task<byte[]> FetchRawBlockAsync(ulong reqId, ulong messageId)
        {
            return FetchRawBlockAsync(reqId, messageId, CancellationToken.None);
        }

        public Task<byte[]> FetchRawBlockAsync(ulong reqId, ulong messageId, CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateMessageId(messageId);
            return SendJsonBackBytesAsync(WSTMQAction.TMQFetchRaw, new WSTMQFetchBlockReq
            {
                ReqId = reqId,
                MessageId = messageId
            }, reqId, cancellationToken);
        }

        public Task<WSTMQCommitResp> CommitAsync()
        {
            return CommitAsync(_GetReqId(), CancellationToken.None);
        }

        public Task<WSTMQCommitResp> CommitAsync(CancellationToken cancellationToken)
        {
            return CommitAsync(_GetReqId(), cancellationToken);
        }

        public Task<WSTMQCommitResp> CommitAsync(ulong reqId)
        {
            return CommitAsync(reqId, CancellationToken.None);
        }

        public Task<WSTMQCommitResp> CommitAsync(ulong reqId, CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            return SendJsonBackJsonAsync<WSTMQCommitReq, WSTMQCommitResp>(WSTMQAction.TMQCommit,
                new WSTMQCommitReq { ReqId = reqId }, reqId, cancellationToken);
        }

        public Task<WSTMQUnsubscribeResp> UnsubscribeAsync()
        {
            return UnsubscribeAsync(_GetReqId(), CancellationToken.None);
        }

        public Task<WSTMQUnsubscribeResp> UnsubscribeAsync(CancellationToken cancellationToken)
        {
            return UnsubscribeAsync(_GetReqId(), cancellationToken);
        }

        public Task<WSTMQUnsubscribeResp> UnsubscribeAsync(ulong reqId)
        {
            return UnsubscribeAsync(reqId, CancellationToken.None);
        }

        public Task<WSTMQUnsubscribeResp> UnsubscribeAsync(ulong reqId, CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            return SendJsonBackJsonAsync<WSTMQUnsubscribeReq, WSTMQUnsubscribeResp>(WSTMQAction.TMQUnsubscribe,
                new WSTMQUnsubscribeReq { ReqId = reqId }, reqId, cancellationToken);
        }

        public Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(string topic)
        {
            return AssignmentAsync(_GetReqId(), topic, CancellationToken.None);
        }

        public Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(string topic, CancellationToken cancellationToken)
        {
            return AssignmentAsync(_GetReqId(), topic, cancellationToken);
        }

        public Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(ulong reqId, string topic)
        {
            return AssignmentAsync(reqId, topic, CancellationToken.None);
        }

        public async Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(ulong reqId, string topic,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopic(topic, nameof(topic));
            var response = await SendJsonBackJsonAsync<WSTMQGetTopicAssignmentReq, WSTMQGetTopicAssignmentResp>(
                WSTMQAction.TMQGetTopicAssignment, new WSTMQGetTopicAssignmentReq
                {
                    ReqId = reqId,
                    Topic = topic
                }, reqId, cancellationToken).ConfigureAwait(false);
            ValidateAssignmentResponse(response);
            return response;
        }

        public Task<WSTMQOffsetSeekResp> SeekAsync(string topic, int vgroupId, long offset)
        {
            return SeekAsync(_GetReqId(), topic, vgroupId, offset, CancellationToken.None);
        }

        public Task<WSTMQOffsetSeekResp> SeekAsync(string topic, int vgroupId, long offset,
            CancellationToken cancellationToken)
        {
            return SeekAsync(_GetReqId(), topic, vgroupId, offset, cancellationToken);
        }

        public Task<WSTMQOffsetSeekResp> SeekAsync(ulong reqId, string topic, int vgroupId, long offset)
        {
            return SeekAsync(reqId, topic, vgroupId, offset, CancellationToken.None);
        }

        public Task<WSTMQOffsetSeekResp> SeekAsync(ulong reqId, string topic, int vgroupId, long offset,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopic(topic, nameof(topic));
            ValidateVgroupId(vgroupId);
            return SendJsonBackJsonAsync<WSTMQOffsetSeekReq, WSTMQOffsetSeekResp>(WSTMQAction.TMQSeek,
                new WSTMQOffsetSeekReq
                {
                    ReqId = reqId,
                    Topic = topic,
                    VGroupId = vgroupId,
                    Offset = offset
                }, reqId, cancellationToken);
        }

        public Task<WSTMQCommitOffsetResp> CommitOffsetAsync(string topic, int vgroupId, long offset)
        {
            return CommitOffsetAsync(_GetReqId(), topic, vgroupId, offset, CancellationToken.None);
        }

        public Task<WSTMQCommitOffsetResp> CommitOffsetAsync(string topic, int vgroupId, long offset,
            CancellationToken cancellationToken)
        {
            return CommitOffsetAsync(_GetReqId(), topic, vgroupId, offset, cancellationToken);
        }

        public Task<WSTMQCommitOffsetResp> CommitOffsetAsync(ulong reqId, string topic, int vgroupId, long offset)
        {
            return CommitOffsetAsync(reqId, topic, vgroupId, offset, CancellationToken.None);
        }

        public async Task<WSTMQCommitOffsetResp> CommitOffsetAsync(ulong reqId, string topic, int vgroupId, long offset,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopic(topic, nameof(topic));
            ValidateVgroupId(vgroupId);
            var response = await SendJsonBackJsonAsync<WSTMQCommitOffsetReq, WSTMQCommitOffsetResp>(
                WSTMQAction.TMQCommitOffset,
                new WSTMQCommitOffsetReq
                {
                    ReqId = reqId,
                    Topic = topic,
                    VGroupId = vgroupId,
                    Offset = offset
                }, reqId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Topic, topic, StringComparison.Ordinal) ||
                response.VGroupId != vgroupId || response.Offset != offset)
            {
                throw CloseForUnexpectedMessage("TMQ commit_offset response does not match the request");
            }

            return response;
        }

        public Task<WSTMQCommittedResp> CommittedAsync(List<WSTopicVgroupId> tvIds)
        {
            return CommittedAsync(_GetReqId(), tvIds, CancellationToken.None);
        }

        public Task<WSTMQCommittedResp> CommittedAsync(List<WSTopicVgroupId> tvIds,
            CancellationToken cancellationToken)
        {
            return CommittedAsync(_GetReqId(), tvIds, cancellationToken);
        }

        public Task<WSTMQCommittedResp> CommittedAsync(ulong reqId, List<WSTopicVgroupId> tvIds)
        {
            return CommittedAsync(reqId, tvIds, CancellationToken.None);
        }

        public async Task<WSTMQCommittedResp> CommittedAsync(ulong reqId, List<WSTopicVgroupId> tvIds,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopicVgroupIds(tvIds);
            var response = await SendJsonBackJsonAsync<WSTMQCommittedReq, WSTMQCommittedResp>(
                WSTMQAction.TMQCommitted,
                new WSTMQCommittedReq
                {
                    ReqId = reqId,
                    TopicVgroupIds = new List<WSTopicVgroupId>(tvIds)
                }, reqId, cancellationToken).ConfigureAwait(false);
            ValidateOffsetResponse(response.Committed, tvIds.Count, "committed");
            return response;
        }

        public Task<WSTMQPositionResp> PositionAsync(List<WSTopicVgroupId> tvIds)
        {
            return PositionAsync(_GetReqId(), tvIds, CancellationToken.None);
        }

        public Task<WSTMQPositionResp> PositionAsync(List<WSTopicVgroupId> tvIds,
            CancellationToken cancellationToken)
        {
            return PositionAsync(_GetReqId(), tvIds, cancellationToken);
        }

        public Task<WSTMQPositionResp> PositionAsync(ulong reqId, List<WSTopicVgroupId> tvIds)
        {
            return PositionAsync(reqId, tvIds, CancellationToken.None);
        }

        public async Task<WSTMQPositionResp> PositionAsync(ulong reqId, List<WSTopicVgroupId> tvIds,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            ValidateTopicVgroupIds(tvIds);
            var response = await SendJsonBackJsonAsync<WSTMQPositionReq, WSTMQPositionResp>(WSTMQAction.TMQPosition,
                new WSTMQPositionReq
                {
                    ReqId = reqId,
                    TopicVgroupIds = new List<WSTopicVgroupId>(tvIds)
                }, reqId, cancellationToken).ConfigureAwait(false);
            ValidateOffsetResponse(response.Position, tvIds.Count, "position");
            return response;
        }

        public Task<WSTMQListTopicsResp> SubscriptionAsync()
        {
            return SubscriptionAsync(_GetReqId(), CancellationToken.None);
        }

        public Task<WSTMQListTopicsResp> SubscriptionAsync(CancellationToken cancellationToken)
        {
            return SubscriptionAsync(_GetReqId(), cancellationToken);
        }

        public Task<WSTMQListTopicsResp> SubscriptionAsync(ulong reqId)
        {
            return SubscriptionAsync(reqId, CancellationToken.None);
        }

        public async Task<WSTMQListTopicsResp> SubscriptionAsync(ulong reqId,
            CancellationToken cancellationToken)
        {
            reqId = NormalizeRequestId(reqId);
            var response = await SendJsonBackJsonAsync<WSTMQListTopicsReq, WSTMQListTopicsResp>(
                    WSTMQAction.TMQListTopics, new WSTMQListTopicsReq { ReqId = reqId }, reqId, cancellationToken)
                .ConfigureAwait(false);
            ValidateResponseTopics(response.Topics);
            return response;
        }

        private static void ValidateTopics(IReadOnlyList<string> topics)
        {
            if (topics == null) throw new ArgumentNullException(nameof(topics));
            if (topics.Count == 0) throw new ArgumentException("topics cannot be empty", nameof(topics));
            if (topics.Count > MaximumCollectionItems)
            {
                throw new ArgumentException($"topics cannot contain more than {MaximumCollectionItems} items",
                    nameof(topics));
            }

            for (var i = 0; i < topics.Count; i++)
            {
                ValidateTopic(topics[i], "topics[" + i + "]");
            }
        }

        private static void ValidateTopic(string topic, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentException("topic cannot be empty", parameterName);
            }

            if (topic.Length > MaximumTopicLength)
            {
                throw new ArgumentException($"topic cannot exceed {MaximumTopicLength} characters", parameterName);
            }
        }

        private static void ValidateVgroupId(int vgroupId)
        {
            if (vgroupId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(vgroupId), "vgroupId cannot be negative.");
            }
        }

        private static void ValidateMessageId(ulong messageId)
        {
            if (messageId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(messageId),
                    "messageId must be greater than zero.");
            }
        }

        private static ulong NormalizeRequestId(ulong reqId)
        {
            return reqId == 0 ? _GetReqId() : reqId;
        }

        private static void ValidateTopicVgroupIds(IReadOnlyList<WSTopicVgroupId> tvIds)
        {
            if (tvIds == null) throw new ArgumentNullException(nameof(tvIds));
            if (tvIds.Count == 0) throw new ArgumentException("topic/vgroup list cannot be empty", nameof(tvIds));
            if (tvIds.Count > MaximumCollectionItems)
            {
                throw new ArgumentException(
                    $"topic/vgroup list cannot contain more than {MaximumCollectionItems} items", nameof(tvIds));
            }

            for (var i = 0; i < tvIds.Count; i++)
            {
                var item = tvIds[i];
                if (item == null)
                {
                    throw new ArgumentException("topic/vgroup list contains a null item", nameof(tvIds));
                }

                ValidateTopic(item.Topic, "tvIds[" + i + "].Topic");
                ValidateVgroupId(item.VGroupId);
            }
        }

        private void ValidatePollResponse(WSTMQPollResp response)
        {
            if (response == null)
            {
                throw CloseForUnexpectedMessage("TMQ poll response is empty");
            }

            if (!response.HaveMessage)
            {
                return;
            }

            if (response.MessageId == 0 || string.IsNullOrWhiteSpace(response.Topic) ||
                response.Topic.Length > MaximumTopicLength || response.VgroupId < 0 ||
                response.MessageType != (int)TMQ_RES.TMQ_RES_DATA &&
                response.MessageType != (int)TMQ_RES.TMQ_RES_TABLE_META &&
                response.MessageType != (int)TMQ_RES.TMQ_RES_METADATA &&
                response.MessageType != (int)TMQ_RES.TMQ_RES_RAWDATA)
            {
                throw CloseForUnexpectedMessage("TMQ poll response contains invalid message metadata");
            }
        }

        private void ValidateAssignmentResponse(WSTMQGetTopicAssignmentResp response)
        {
            if (response == null || response.Assignment == null ||
                response.Assignment.Count > MaximumCollectionItems)
            {
                throw CloseForUnexpectedMessage("TMQ assignment response contains an invalid assignment list");
            }

            for (var i = 0; i < response.Assignment.Count; i++)
            {
                var assignment = response.Assignment[i];
                if (assignment == null || assignment.VGroupId < 0)
                {
                    throw CloseForUnexpectedMessage(
                        $"TMQ assignment response contains invalid metadata at index {i}");
                }
            }
        }

        private void ValidateOffsetResponse(IReadOnlyCollection<long> offsets, int expectedCount, string operation)
        {
            if (offsets == null || offsets.Count != expectedCount)
            {
                throw CloseForUnexpectedMessage(
                    $"TMQ {operation} response count does not match the request count");
            }
        }

        private void ValidateResponseTopics(IReadOnlyList<string> topics)
        {
            if (topics == null || topics.Count > MaximumCollectionItems)
            {
                throw CloseForUnexpectedMessage("TMQ subscription response contains an invalid topic list");
            }

            for (var i = 0; i < topics.Count; i++)
            {
                var topic = topics[i];
                if (string.IsNullOrWhiteSpace(topic) || topic.Length > MaximumTopicLength)
                {
                    throw CloseForUnexpectedMessage(
                        $"TMQ subscription response contains an invalid topic at index {i}");
                }
            }
        }

        private void ValidateInstances(IReadOnlyList<string> instances)
        {
            if (instances == null)
            {
                return;
            }

            if (instances.Count > AdapterHAHelper.MaximumExaminedInstances)
            {
                throw CloseForUnexpectedMessage("TMQ subscribe response contains too many adapter instances");
            }

            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (string.IsNullOrWhiteSpace(instance) || instance.Length > MaximumInstanceLength)
                {
                    throw CloseForUnexpectedMessage(
                        $"TMQ subscribe response contains an invalid adapter instance at index {i}");
                }
            }
        }
    }
}
