using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{

    public class TMQConnectionAsync : BaseConnectionAsync
    {
        public TMQConnectionAsync(TMQOptions options, TimeSpan connectTimeout = default(TimeSpan),
            TimeSpan readTimeout = default(TimeSpan), TimeSpan writeTimeout = default(TimeSpan))
            : base(GetUrl(options), connectTimeout, readTimeout, writeTimeout, options.TDEnableCompression == "true")
        {
        }

        private static string GetUrl(TMQOptions options)
        {
            string value = "ws";
            string value2 = options.TDConnectPort;
            if (options.TDUseSSL == "true")
            {
                value = "wss";
                if (string.IsNullOrEmpty(options.TDConnectPort))
                {
                    value2 = "443";
                }
            }
            else if (string.IsNullOrEmpty(options.TDConnectPort))
            {
                value2 = "6041";
            }

            if (!string.IsNullOrEmpty(options.TDToken))
            {
                return $"{value}://{options.TDConnectIp}:{value2}/rest/tmq?token={options.TDToken}";
            }

            return $"{value}://{options.TDConnectIp}:{value2}/rest/tmq";
        }

        public async Task ConnectAsync()
        {
            await ClientConnectAsync().ConfigureAwait(false);
        }

        public async Task<WSTMQSubscribeResp> SubscribeAsync(List<string> topics, TMQOptions options)
        {
            return await SubscribeAsync(_GetReqId(), topics, options).ConfigureAwait(continueOnCapturedContext: false);
        }

        private async Task<WSTMQSubscribeResp> SubscribeAsync(ulong reqId, List<string> topics, TMQOptions options)
        {
            return await SendJsonBackJsonAsync<WSTMQSubscribeReq, WSTMQSubscribeResp>("subscribe", new WSTMQSubscribeReq
            {
                ReqId = reqId,
                User = options.TDConnectUser,
                Password = options.TDConnectPasswd,
                Db = options.TDDatabase,
                GroupId = options.GroupId,
                ClientId = options.ClientId,
                OffsetRest = options.AutoOffsetReset,
                Topics = topics,
                AutoCommit = "false",
                AutoCommitIntervalMs = options.AutoCommitIntervalMs,
                WithTableName = options.MsgWithTableName,
                SessionTimeoutMs = options.SessionTimeoutMs,
                MaxPollIntervalMs = options.MaxPollIntervalMs
            });
        }

        public async Task<WSTMQPollResp> PollAsync(long blockingTime)
        {
            return await PollAsync(_GetReqId(), blockingTime);
        }

        public async Task<WSTMQPollResp> PollAsync(ulong reqId, long blockingTime)
        {
            return await SendJsonBackJsonAsync<WSTMQPollReq, WSTMQPollResp>(WSTMQAction.TMQPoll, new WSTMQPollReq
            {
                ReqId = reqId,
                BlockingTime = blockingTime
            });
        }

        public async Task<byte[]> FetchBlockAsync(ulong reqId, ulong messageId)
        {
            return await SendJsonBackBytesAsync(WSTMQAction.TMQFetchBlock, new WSTMQFetchBlockReq
            {
                ReqId = reqId,
                MessageId = messageId
            });
        }

        public async Task<byte[]> FetchRawBlockAsync(ulong messageId)
        {
            return await FetchRawBlockAsync(_GetReqId(), messageId);
        }

        public async Task<byte[]> FetchRawBlockAsync(ulong reqId, ulong messageId)
        {
            return await SendJsonBackBytesAsync(WSTMQAction.TMQFetchRaw, new WSTMQFetchBlockReq
            {
                ReqId = reqId,
                MessageId = messageId
            });
        }

        public async Task<WSTMQCommitResp> CommitAsync()
        {
            return await CommitAsync(_GetReqId()).ConfigureAwait(continueOnCapturedContext: false);
        }

        public async Task<WSTMQCommitResp> CommitAsync(ulong reqId)
        {
            return await SendJsonBackJsonAsync<WSTMQCommitReq, WSTMQCommitResp>(WSTMQAction.TMQCommit, new WSTMQCommitReq
            {
                ReqId = reqId
            });
        }

        public async Task<WSTMQUnsubscribeResp> UnsubscribeAsync()
        {
            return await UnsubscribeAsync(_GetReqId());
        }

        public async Task<WSTMQUnsubscribeResp> UnsubscribeAsync(ulong reqId)
        {
            return await SendJsonBackJsonAsync<WSTMQUnsubscribeReq, WSTMQUnsubscribeResp>(WSTMQAction.TMQUnsubscribe,
                new WSTMQUnsubscribeReq
                {
                    ReqId = reqId
                });
        }

        public async Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(string topic)
        {
            return await AssignmentAsync(_GetReqId(), topic);
        }

        public async Task<WSTMQGetTopicAssignmentResp> AssignmentAsync(ulong reqId, string topic)
        {
            return await SendJsonBackJsonAsync<WSTMQGetTopicAssignmentReq, WSTMQGetTopicAssignmentResp>(WSTMQAction.TMQGetTopicAssignment,
                new WSTMQGetTopicAssignmentReq
                {
                    ReqId = reqId,
                    Topic = topic
                });
        }

        public async Task<WSTMQOffsetSeekResp> SeekAsync(string topic, int vgroupId, long offset)
        {
            return await SeekAsync(_GetReqId(), topic, vgroupId, offset);
        }

        public async Task<WSTMQOffsetSeekResp> SeekAsync(ulong reqId, string topic, int vgroupId, long offset)
        {
            return await SendJsonBackJsonAsync<WSTMQOffsetSeekReq, WSTMQOffsetSeekResp>(WSTMQAction.TMQSeek, new WSTMQOffsetSeekReq
            {
                ReqId = reqId,
                Topic = topic,
                VGroupId = vgroupId,
                Offset = offset
            });
        }

        public async Task<WSTMQCommitOffsetResp> CommitOffsetAsync(string topic, int vgroupId, long offset)
        {
            return await CommitOffsetAsync(_GetReqId(), topic, vgroupId, offset);
        }

        public async Task<WSTMQCommitOffsetResp> CommitOffsetAsync(ulong reqId, string topic, int vgroupId, long offset)
        {
            return await SendJsonBackJsonAsync<WSTMQCommitOffsetReq, WSTMQCommitOffsetResp>(WSTMQAction.TMQCommitOffset,
                new WSTMQCommitOffsetReq
                {
                    ReqId = reqId,
                    Topic = topic,
                    VGroupId = vgroupId,
                    Offset = offset
                });
        }

        public async Task<WSTMQCommittedResp> CommittedAsync(List<WSTopicVgroupId> tvIds)
        {
            return await CommittedAsync(_GetReqId(), tvIds);
        }

        public async Task<WSTMQCommittedResp> CommittedAsync(ulong reqId, List<WSTopicVgroupId> tvIds)
        {
            return await SendJsonBackJsonAsync<WSTMQCommittedReq, WSTMQCommittedResp>(WSTMQAction.TMQCommitted, new WSTMQCommittedReq
            {
                ReqId = reqId,
                TopicVgroupIds = tvIds
            });
        }

        public async Task<WSTMQPositionResp> PositionAsync(List<WSTopicVgroupId> tvIds)
        {
            return await PositionAsync(_GetReqId(), tvIds);
        }

        public async Task<WSTMQPositionResp> PositionAsync(ulong reqId, List<WSTopicVgroupId> tvIds)
        {
            return await SendJsonBackJsonAsync<WSTMQPositionReq, WSTMQPositionResp>(WSTMQAction.TMQPosition, new WSTMQPositionReq
            {
                ReqId = reqId,
                TopicVgroupIds = tvIds
            });
        }

        public async Task<WSTMQListTopicsResp> SubscriptionAsync()
        {
            return await SubscriptionAsync(_GetReqId());
        }

        public async Task<WSTMQListTopicsResp> SubscriptionAsync(ulong reqId)
        {
            return await SendJsonBackJsonAsync<WSTMQListTopicsReq, WSTMQListTopicsResp>(WSTMQAction.TMQListTopics,
                new WSTMQListTopicsReq
                {
                    ReqId = reqId
                });
        }
    }
}
