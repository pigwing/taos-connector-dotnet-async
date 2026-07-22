using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;
using Test.Fixture;
using Xunit;

namespace Driver.Test.Client.TMQ
{
    [Collection("WebSocket async collection")]
    public sealed class TMQAsyncIntegration
    {
        [Fact]
        public async Task WebSocketAsyncTmqLifecycleAndConcurrentDispatchIntegrationTest()
        {
            const string database = "ws_async_tmq_integration_test";
            const string topic = "ws_async_tmq_integration_topic";
            var builder = new ConnectionStringBuilder(TestConnectionOptions.WebSocketConnectionString());
            using (var setupClient = await DbDriver.OpenAsync(builder))
            {
                TMQConnectionAsync? connection = null;
                try
                {
                    await setupClient.ExecAsync($"drop topic if exists {topic}");
                    await setupClient.ExecAsync($"drop database if exists {database}");
                    await setupClient.ExecAsync($"create database {database}");
                    await setupClient.ExecAsync($"use {database}");
                    await setupClient.ExecAsync("create table readings(ts timestamp, c1 int)");
                    await setupClient.ExecAsync($"create topic {topic} as select ts, c1 from readings");

                    var config = TestConnectionOptions.WebSocketTmqConfig(false);
                    config["td.connect.db"] = database;
                    config["group.id"] = "ws_async_tmq_" + Guid.NewGuid().ToString("N");
                    config["client.id"] = "ws_async_tmq_client";
                    var options = new TMQOptions(config);
                    connection = new TMQConnectionAsync(options, TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
                    await connection.ConnectAsync();

                    await connection.SubscribeAsync(new List<string> { topic }, options);
                    var assignment = await connection.AssignmentAsync(topic);
                    Assert.NotEmpty(assignment.Assignment);

                    var topicVgroups = new List<WSTopicVgroupId>(assignment.Assignment.Count);
                    for (var i = 0; i < assignment.Assignment.Count; i++)
                    {
                        topicVgroups.Add(new WSTopicVgroupId
                        {
                            Topic = topic,
                            VGroupId = assignment.Assignment[i].VGroupId
                        });
                    }

                    var initialPositions = await connection.PositionAsync(topicVgroups);
                    Assert.Equal(topicVgroups.Count, initialPositions.Position.Count);

                    using (var canceled = new CancellationTokenSource())
                    {
                        canceled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                            () => connection.PollAsync(1000, canceled.Token));
                    }

                    Assert.True(connection.IsAvailable());
                    await setupClient.ExecAsync("insert into readings values(now, 42)");

                    var pollTask = connection.PollAsync(5000);
                    var subscriptionTask = connection.SubscriptionAsync();
                    await Task.WhenAll(pollTask, subscriptionTask);

                    var subscription = await subscriptionTask;
                    Assert.Contains(topic, subscription.Topics);

                    var poll = await pollTask;
                    for (var attempt = 0; !poll.HaveMessage && attempt < 4; attempt++)
                    {
                        poll = await connection.PollAsync(5000);
                    }

                    Assert.True(poll.HaveMessage);
                    Assert.Equal(topic, poll.Topic);
                    Assert.True(poll.MessageId > 0);

                    var rawBlock = await connection.FetchRawBlockAsync(poll.MessageId);
                    Assert.NotEmpty(rawBlock);

                    var commitOffset = await connection.CommitOffsetAsync(topic, poll.VgroupId, poll.Offset);
                    Assert.Equal(topic, commitOffset.Topic);
                    Assert.Equal(poll.VgroupId, commitOffset.VGroupId);
                    Assert.Equal(poll.Offset, commitOffset.Offset);

                    var committed = await connection.CommittedAsync(topicVgroups);
                    Assert.Equal(topicVgroups.Count, committed.Committed.Count);
                    var positions = await connection.PositionAsync(topicVgroups);
                    Assert.Equal(topicVgroups.Count, positions.Position.Count);

                    var polledAssignment = assignment.Assignment.Find(item => item.VGroupId == poll.VgroupId);
                    Assert.NotNull(polledAssignment);
                    await connection.SeekAsync(topic, poll.VgroupId, polledAssignment!.Begin);
                    await connection.CommitAsync();
                    await connection.UnsubscribeAsync();
                    await connection.CloseAsync();
                    connection = null;
                }
                finally
                {
                    if (connection != null)
                    {
                        await connection.CloseAsync();
                    }

                    if (setupClient.ConnectionAvailable())
                    {
                        await setupClient.ExecAsync($"drop topic if exists {topic}");
                        await setupClient.ExecAsync($"drop database if exists {database}");
                    }
                }
            }
        }
    }
}
