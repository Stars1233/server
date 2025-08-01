﻿using Bit.Core.AdminConsole.OrganizationFeatures.OrganizationUsers.Interfaces;
using Bit.Core.AdminConsole.OrganizationFeatures.OrganizationUsers.RestoreUser.v1;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Repositories;
using Bit.Scim.Models;
using Bit.Scim.Users.Interfaces;

namespace Bit.Scim.Users;

public class PatchUserCommand : IPatchUserCommand
{
    private readonly IOrganizationUserRepository _organizationUserRepository;
    private readonly IRestoreOrganizationUserCommand _restoreOrganizationUserCommand;
    private readonly ILogger<PatchUserCommand> _logger;
    private readonly IRevokeOrganizationUserCommand _revokeOrganizationUserCommand;

    public PatchUserCommand(IOrganizationUserRepository organizationUserRepository,
        IRestoreOrganizationUserCommand restoreOrganizationUserCommand,
        ILogger<PatchUserCommand> logger,
        IRevokeOrganizationUserCommand revokeOrganizationUserCommand)
    {
        _organizationUserRepository = organizationUserRepository;
        _restoreOrganizationUserCommand = restoreOrganizationUserCommand;
        _logger = logger;
        _revokeOrganizationUserCommand = revokeOrganizationUserCommand;
    }

    public async Task PatchUserAsync(Guid organizationId, Guid id, ScimPatchModel model)
    {
        var orgUser = await _organizationUserRepository.GetByIdAsync(id);
        if (orgUser == null || orgUser.OrganizationId != organizationId)
        {
            throw new NotFoundException("User not found.");
        }

        var operationHandled = false;
        foreach (var operation in model.Operations)
        {
            // Replace operations
            if (operation.Op?.ToLowerInvariant() == "replace")
            {
                // Active from path
                if (operation.Path?.ToLowerInvariant() == "active")
                {
                    var active = operation.Value.ToString()?.ToLowerInvariant();
                    var handled = await HandleActiveOperationAsync(orgUser, active == "true");
                    if (!operationHandled)
                    {
                        operationHandled = handled;
                    }
                }
                // Active from value object
                else if (string.IsNullOrWhiteSpace(operation.Path) &&
                    operation.Value.TryGetProperty("active", out var activeProperty))
                {
                    var handled = await HandleActiveOperationAsync(orgUser, activeProperty.GetBoolean());
                    if (!operationHandled)
                    {
                        operationHandled = handled;
                    }
                }
            }
        }

        if (!operationHandled)
        {
            _logger.LogWarning("User patch operation not handled: {operation} : ",
                string.Join(", ", model.Operations.Select(o => $"{o.Op}:{o.Path}")));
        }
    }

    private async Task<bool> HandleActiveOperationAsync(Core.Entities.OrganizationUser orgUser, bool active)
    {
        if (active && orgUser.Status == OrganizationUserStatusType.Revoked)
        {
            await _restoreOrganizationUserCommand.RestoreUserAsync(orgUser, EventSystemUser.SCIM);
            return true;
        }
        else if (!active && orgUser.Status != OrganizationUserStatusType.Revoked)
        {
            await _revokeOrganizationUserCommand.RevokeUserAsync(orgUser, EventSystemUser.SCIM);
            return true;
        }
        return false;
    }
}
