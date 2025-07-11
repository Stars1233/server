﻿#nullable enable
using AutoMapper;
using Bit.Core.Enums;
using Bit.Core.Models.Data;
using Bit.Core.NotificationCenter.Models.Data;
using Bit.Core.NotificationCenter.Models.Filter;
using Bit.Core.NotificationCenter.Repositories;
using Bit.Infrastructure.EntityFramework.NotificationCenter.Models;
using Bit.Infrastructure.EntityFramework.NotificationCenter.Repositories.Queries;
using Bit.Infrastructure.EntityFramework.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bit.Infrastructure.EntityFramework.NotificationCenter.Repositories;

public class NotificationRepository : Repository<Core.NotificationCenter.Entities.Notification, Notification, Guid>,
    INotificationRepository
{
    public NotificationRepository(IServiceScopeFactory serviceScopeFactory, IMapper mapper)
        : base(serviceScopeFactory, mapper, context => context.Notifications)
    {
    }

    public async Task<IEnumerable<Core.NotificationCenter.Entities.Notification>> GetByUserIdAsync(Guid userId,
        ClientType clientType)
    {
        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var dbContext = GetDatabaseContext(scope);

        var notificationStatusDetailsViewQuery = new NotificationStatusDetailsViewQuery(userId, clientType);

        var notifications = await notificationStatusDetailsViewQuery.Run(dbContext)
            .OrderByDescending(n => n.Priority)
            .ThenByDescending(n => n.CreationDate)
            .ToListAsync();

        return Mapper.Map<List<Core.NotificationCenter.Entities.Notification>>(notifications);
    }

    public async Task<PagedResult<NotificationStatusDetails>> GetByUserIdAndStatusAsync(Guid userId,
        ClientType clientType, NotificationStatusFilter? statusFilter, PageOptions pageOptions)
    {
        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var dbContext = GetDatabaseContext(scope);

        if (!int.TryParse(pageOptions.ContinuationToken, out var pageNumber))
        {
            pageNumber = 1;
        }

        var notificationStatusDetailsViewQuery = new NotificationStatusDetailsViewQuery(userId, clientType);

        var query = notificationStatusDetailsViewQuery.Run(dbContext);
        if (statusFilter != null && (statusFilter.Read != null || statusFilter.Deleted != null))
        {
            query = from n in query
                    where (statusFilter.Read == null ||
                           (statusFilter.Read == true ? n.ReadDate != null : n.ReadDate == null)) &&
                          (statusFilter.Deleted == null ||
                           (statusFilter.Deleted == true ? n.DeletedDate != null : n.DeletedDate == null))
                    select n;
        }

        var results = await query
            .OrderByDescending(n => n.Priority)
            .ThenByDescending(n => n.CreationDate)
            .Skip(pageOptions.PageSize * (pageNumber - 1))
            .Take(pageOptions.PageSize)
            .ToListAsync();

        return new PagedResult<NotificationStatusDetails>
        {
            Data = results,
            ContinuationToken = results.Count < pageOptions.PageSize ? null : (pageNumber + 1).ToString()
        };
    }

    public async Task<IEnumerable<Guid>> MarkNotificationsAsDeletedByTask(Guid taskId)
    {
        await using var scope = ServiceScopeFactory.CreateAsyncScope();
        var dbContext = GetDatabaseContext(scope);

        var notifications = await dbContext.Notifications
            .Where(n => n.TaskId == taskId)
            .ToListAsync();

        var notificationIds = notifications.Select(n => n.Id).ToList();

        var statuses = await dbContext.Set<NotificationStatus>()
            .Where(ns => notificationIds.Contains(ns.NotificationId))
            .ToListAsync();

        var now = DateTime.UtcNow;

        // Update existing statuses and add missing ones
        foreach (var notification in notifications)
        {
            var status = statuses.FirstOrDefault(s => s.NotificationId == notification.Id);
            if (status != null)
            {
                if (status.DeletedDate == null)
                {
                    status.DeletedDate = now;
                }
            }
            else if (notification.UserId.HasValue)
            {
                dbContext.Set<NotificationStatus>().Add(new NotificationStatus
                {
                    NotificationId = notification.Id,
                    UserId = (Guid)notification.UserId,
                    DeletedDate = now
                });
            }
        }

        await dbContext.SaveChangesAsync();

        var userIds = notifications
            .Select(n => n.UserId)
            .Where(u => u.HasValue)
            .ToList();

        return (IEnumerable<Guid>)userIds;
    }
}
