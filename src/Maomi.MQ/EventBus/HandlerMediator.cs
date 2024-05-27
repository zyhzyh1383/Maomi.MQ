﻿// <copyright file="HandlerMediator.cs" company="Maomi">
// Copyright (c) Maomi. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Github link: https://github.com/whuanle/Maomi.MQ
// </copyright>

using Maomi.MQ.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Maomi.MQ.EventBus;

/// <summary>
/// Event mediator, used to generate a sequential event execution flow and compensation flow.<br />
/// 事件中介者，用于生成有顺序的事件执行流程和补偿流程.
/// </summary>
/// <typeparam name="TEvent">Event mode.</typeparam>
public class HandlerMediator<TEvent> : IHandlerMediator<TEvent>
    where TEvent : class
{
    private readonly IServiceProvider _serviceProvider;
    private readonly EventInfo _eventInfo;
    private readonly DiagnosticsWriter _diagnosticsWriter = new ConsumerDiagnosticsWriter();

    /// <summary>
    /// Initializes a new instance of the <see cref="HandlerMediator{TEvent}"/> class.
    /// </summary>
    /// <param name="serviceProvider"></param>
    public HandlerMediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _eventInfo = _serviceProvider.GetRequiredKeyedService<EventInfo>(typeof(TEvent));
    }

    /// <inheritdoc/>
    public async Task ExecuteAsync(EventBody<TEvent> eventBody, CancellationToken cancellationToken)
    {
        var logger = _serviceProvider.GetRequiredService<ILogger<TEvent>>();
        List<ActivityInit> eventHandlers = new(_eventInfo.Handlers.Count);

        ActivityTagsCollection tags = new() { { DiagnosticName.Event.Id, eventBody.Id } };
        using Activity? activity = _diagnosticsWriter.WriteStarted(DiagnosticName.Activity.Eventbus, DateTimeOffset.Now, tags);

        // Build execution flow.
        // 构建执行链.
        // 1 => 2 => 3 =>...
        foreach (var handler in _eventInfo.Handlers)
        {
            ActivityTagsCollection executetTags = new()
            {
                { DiagnosticName.Event.Id, eventBody.Id },
                { "order", handler.Key },
                { "event.name", handler.Value.Name }
            };

            using var executeActivity = _diagnosticsWriter.WriteStarted(DiagnosticName.Activity.Execute, DateTimeOffset.Now, executetTags);

            try
            {
                // Forward execution。
                // 正向执行.
                var eventHandler = _serviceProvider.GetRequiredService(handler.Value) as IEventHandler<TEvent>;
                ArgumentNullException.ThrowIfNull(eventHandler);

                eventHandlers.Add(new ActivityInit(activity, eventHandler));

                await eventHandler.ExecuteAsync(eventBody, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                _diagnosticsWriter.WriteStopped(executeActivity, DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An exception occurred while executing the event,event type:[{Name}], event id:[{Id}]", typeof(TEvent).Name, eventBody.Id);
                _diagnosticsWriter.WriteException(executeActivity, ex);

                // Rollback.
                // 回滚.
                for (int j = eventHandlers.Count - 1; j >= 0; j--)
                {
                    var eventHandler = eventHandlers[j];
                    try
                    {
                        await eventHandler.EventHandler.CancelAsync(eventBody, cancellationToken);
                    }
                    catch (Exception cancelEx)
                    {
                        _diagnosticsWriter.WriteException(eventHandler.Activity, cancelEx);
                        throw;
                    }
                    finally
                    {
                        _diagnosticsWriter.WriteStopped(eventHandler.Activity, DateTimeOffset.Now);
                    }
                }

                throw;
            }
            finally
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                _diagnosticsWriter.WriteStopped(activity, DateTimeOffset.Now);
            }
        }
    }

    private class ActivityInit
    {
        public ActivityInit(Activity? activity, IEventHandler<TEvent> eventHandler)
        {
            Activity = activity;
            EventHandler = eventHandler;
        }

        public Activity? Activity { get; init; }

        public IEventHandler<TEvent> EventHandler { get; init; }
    }
}
