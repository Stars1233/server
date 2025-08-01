﻿// FIXME: Update this file to be null safe and then delete the line below
#nullable disable

using Bit.Api.AdminConsole.Authorization;
using Bit.Api.AdminConsole.Authorization.Requirements;
using Bit.Api.AdminConsole.Models.Request.Organizations;
using Bit.Api.AdminConsole.Models.Response.Organizations;
using Bit.Api.Models.Request.Organizations;
using Bit.Api.Models.Response;
using Bit.Api.Vault.AuthorizationHandlers.Collections;
using Bit.Core;
using Bit.Core.AdminConsole.Enums;
using Bit.Core.AdminConsole.Models.Data.Organizations.Policies;
using Bit.Core.AdminConsole.OrganizationFeatures.OrganizationUsers.Interfaces;
using Bit.Core.AdminConsole.OrganizationFeatures.OrganizationUsers.RestoreUser.v1;
using Bit.Core.AdminConsole.OrganizationFeatures.Policies;
using Bit.Core.AdminConsole.OrganizationFeatures.Policies.PolicyRequirements;
using Bit.Core.AdminConsole.Repositories;
using Bit.Core.Auth.Enums;
using Bit.Core.Auth.Repositories;
using Bit.Core.Billing.Pricing;
using Bit.Core.Context;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.Business;
using Bit.Core.Models.Data.Organizations.OrganizationUsers;
using Bit.Core.OrganizationFeatures.OrganizationSubscriptions.Interface;
using Bit.Core.OrganizationFeatures.OrganizationUsers.Interfaces;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Utilities;
using Core.AdminConsole.OrganizationFeatures.OrganizationUsers.Interfaces;
using Core.AdminConsole.OrganizationFeatures.OrganizationUsers.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bit.Api.AdminConsole.Controllers;

[Route("organizations/{orgId}/users")]
[Authorize("Application")]
public class OrganizationUsersController : Controller
{
    private readonly IOrganizationRepository _organizationRepository;
    private readonly IOrganizationUserRepository _organizationUserRepository;
    private readonly IOrganizationService _organizationService;
    private readonly ICollectionRepository _collectionRepository;
    private readonly IGroupRepository _groupRepository;
    private readonly IUserService _userService;
    private readonly IPolicyRepository _policyRepository;
    private readonly ICurrentContext _currentContext;
    private readonly ICountNewSmSeatsRequiredQuery _countNewSmSeatsRequiredQuery;
    private readonly IUpdateSecretsManagerSubscriptionCommand _updateSecretsManagerSubscriptionCommand;
    private readonly IUpdateOrganizationUserCommand _updateOrganizationUserCommand;
    private readonly IAcceptOrgUserCommand _acceptOrgUserCommand;
    private readonly IAuthorizationService _authorizationService;
    private readonly IApplicationCacheService _applicationCacheService;
    private readonly ISsoConfigRepository _ssoConfigRepository;
    private readonly IOrganizationUserUserDetailsQuery _organizationUserUserDetailsQuery;
    private readonly IRemoveOrganizationUserCommand _removeOrganizationUserCommand;
    private readonly IDeleteClaimedOrganizationUserAccountCommand _deleteClaimedOrganizationUserAccountCommand;
    private readonly IGetOrganizationUsersClaimedStatusQuery _getOrganizationUsersClaimedStatusQuery;
    private readonly IPolicyRequirementQuery _policyRequirementQuery;
    private readonly IFeatureService _featureService;
    private readonly IPricingClient _pricingClient;
    private readonly IConfirmOrganizationUserCommand _confirmOrganizationUserCommand;
    private readonly IRestoreOrganizationUserCommand _restoreOrganizationUserCommand;
    private readonly IInitPendingOrganizationCommand _initPendingOrganizationCommand;
    private readonly IRevokeOrganizationUserCommand _revokeOrganizationUserCommand;

    public OrganizationUsersController(IOrganizationRepository organizationRepository,
        IOrganizationUserRepository organizationUserRepository,
        IOrganizationService organizationService,
        ICollectionRepository collectionRepository,
        IGroupRepository groupRepository,
        IUserService userService,
        IPolicyRepository policyRepository,
        ICurrentContext currentContext,
        ICountNewSmSeatsRequiredQuery countNewSmSeatsRequiredQuery,
        IUpdateSecretsManagerSubscriptionCommand updateSecretsManagerSubscriptionCommand,
        IUpdateOrganizationUserCommand updateOrganizationUserCommand,
        IAcceptOrgUserCommand acceptOrgUserCommand,
        IAuthorizationService authorizationService,
        IApplicationCacheService applicationCacheService,
        ISsoConfigRepository ssoConfigRepository,
        IOrganizationUserUserDetailsQuery organizationUserUserDetailsQuery,
        IRemoveOrganizationUserCommand removeOrganizationUserCommand,
        IDeleteClaimedOrganizationUserAccountCommand deleteClaimedOrganizationUserAccountCommand,
        IGetOrganizationUsersClaimedStatusQuery getOrganizationUsersClaimedStatusQuery,
        IPolicyRequirementQuery policyRequirementQuery,
        IFeatureService featureService,
        IPricingClient pricingClient,
        IConfirmOrganizationUserCommand confirmOrganizationUserCommand,
        IRestoreOrganizationUserCommand restoreOrganizationUserCommand,
        IInitPendingOrganizationCommand initPendingOrganizationCommand,
        IRevokeOrganizationUserCommand revokeOrganizationUserCommand)
    {
        _organizationRepository = organizationRepository;
        _organizationUserRepository = organizationUserRepository;
        _organizationService = organizationService;
        _collectionRepository = collectionRepository;
        _groupRepository = groupRepository;
        _userService = userService;
        _policyRepository = policyRepository;
        _currentContext = currentContext;
        _countNewSmSeatsRequiredQuery = countNewSmSeatsRequiredQuery;
        _updateSecretsManagerSubscriptionCommand = updateSecretsManagerSubscriptionCommand;
        _updateOrganizationUserCommand = updateOrganizationUserCommand;
        _acceptOrgUserCommand = acceptOrgUserCommand;
        _authorizationService = authorizationService;
        _applicationCacheService = applicationCacheService;
        _ssoConfigRepository = ssoConfigRepository;
        _organizationUserUserDetailsQuery = organizationUserUserDetailsQuery;
        _removeOrganizationUserCommand = removeOrganizationUserCommand;
        _deleteClaimedOrganizationUserAccountCommand = deleteClaimedOrganizationUserAccountCommand;
        _getOrganizationUsersClaimedStatusQuery = getOrganizationUsersClaimedStatusQuery;
        _policyRequirementQuery = policyRequirementQuery;
        _featureService = featureService;
        _pricingClient = pricingClient;
        _confirmOrganizationUserCommand = confirmOrganizationUserCommand;
        _restoreOrganizationUserCommand = restoreOrganizationUserCommand;
        _initPendingOrganizationCommand = initPendingOrganizationCommand;
        _revokeOrganizationUserCommand = revokeOrganizationUserCommand;
    }

    [HttpGet("{id}")]
    [Authorize<ManageUsersRequirement>]
    public async Task<OrganizationUserDetailsResponseModel> Get(Guid orgId, Guid id, bool includeGroups = false)
    {
        var (organizationUser, collections) = await _organizationUserRepository.GetDetailsByIdWithCollectionsAsync(id);
        if (organizationUser == null || organizationUser.OrganizationId != orgId)
        {
            throw new NotFoundException();
        }

        var claimedByOrganizationStatus = await GetClaimedByOrganizationStatusAsync(
            organizationUser.OrganizationId,
            [organizationUser.Id]);

        var response = new OrganizationUserDetailsResponseModel(organizationUser, claimedByOrganizationStatus[organizationUser.Id], collections);

        if (includeGroups)
        {
            response.Groups = await _groupRepository.GetManyIdsByUserIdAsync(organizationUser.Id);
        }

        return response;
    }

    /// <summary>
    /// Returns a set of basic information about all members of the organization. This is available to all members of
    /// the organization to manage collections. For this reason, it contains as little information as possible and no
    /// cryptographic keys or other sensitive data.
    /// </summary>
    /// <param name="orgId">Organization identifier</param>
    /// <returns>List of users for the organization.</returns>
    [HttpGet("mini-details")]
    [Authorize<MemberOrProviderRequirement>]
    public async Task<ListResponseModel<OrganizationUserUserMiniDetailsResponseModel>> GetMiniDetails(Guid orgId)
    {
        var organizationUserUserDetails = await _organizationUserRepository.GetManyDetailsByOrganizationAsync(orgId);
        return new ListResponseModel<OrganizationUserUserMiniDetailsResponseModel>(
            organizationUserUserDetails.Select(ou => new OrganizationUserUserMiniDetailsResponseModel(ou)));
    }

    [HttpGet("")]
    public async Task<ListResponseModel<OrganizationUserUserDetailsResponseModel>> Get(Guid orgId, bool includeGroups = false, bool includeCollections = false)
    {
        var request = new OrganizationUserUserDetailsQueryRequest
        {
            OrganizationId = orgId,
            IncludeGroups = includeGroups,
            IncludeCollections = includeCollections
        };

        if ((await _authorizationService.AuthorizeAsync(User, new ManageUsersRequirement())).Succeeded)
        {
            return GetResultListResponseModel(await _organizationUserUserDetailsQuery.Get(request));
        }

        if ((await _authorizationService.AuthorizeAsync(User, new ManageAccountRecoveryRequirement())).Succeeded)
        {
            return GetResultListResponseModel(await _organizationUserUserDetailsQuery.GetAccountRecoveryEnrolledUsers(request));
        }

        throw new NotFoundException();
    }

    private ListResponseModel<OrganizationUserUserDetailsResponseModel> GetResultListResponseModel(IEnumerable<(OrganizationUserUserDetails OrgUser,
                bool TwoFactorEnabled, bool ClaimedByOrganization)> results)
    {
        return new ListResponseModel<OrganizationUserUserDetailsResponseModel>(results
            .Select(result => new OrganizationUserUserDetailsResponseModel(result))
            .ToList());
    }

    [HttpGet("{id}/reset-password-details")]
    [Authorize<ManageAccountRecoveryRequirement>]
    public async Task<OrganizationUserResetPasswordDetailsResponseModel> GetResetPasswordDetails(Guid orgId, Guid id)
    {
        var organizationUser = await _organizationUserRepository.GetByIdAsync(id);
        if (organizationUser is null || organizationUser.UserId is null)
        {
            throw new NotFoundException();
        }

        // Retrieve data necessary for response (KDF, KDF Iterations, ResetPasswordKey)
        // TODO Reset Password - Revisit this and create SPROC to reduce DB calls
        var user = await _userService.GetUserByIdAsync(organizationUser.UserId.Value);
        if (user == null)
        {
            throw new NotFoundException();
        }

        // Retrieve Encrypted Private Key from organization
        var org = await _organizationRepository.GetByIdAsync(orgId);
        if (org == null)
        {
            throw new NotFoundException();
        }

        return new OrganizationUserResetPasswordDetailsResponseModel(new OrganizationUserResetPasswordDetails(organizationUser, user, org));
    }

    [HttpPost("account-recovery-details")]
    [Authorize<ManageAccountRecoveryRequirement>]
    public async Task<ListResponseModel<OrganizationUserResetPasswordDetailsResponseModel>> GetAccountRecoveryDetails(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        var responses = await _organizationUserRepository.GetManyAccountRecoveryDetailsByOrganizationUserAsync(orgId, model.Ids);
        return new ListResponseModel<OrganizationUserResetPasswordDetailsResponseModel>(responses.Select(r => new OrganizationUserResetPasswordDetailsResponseModel(r)));
    }

    [HttpPost("invite")]
    [Authorize<ManageUsersRequirement>]
    public async Task Invite(Guid orgId, [FromBody] OrganizationUserInviteRequestModel model)
    {
        // Check the user has permission to grant access to the collections for the new user
        if (model.Collections?.Any() == true)
        {
            var collections = await _collectionRepository.GetManyByManyIdsAsync(model.Collections.Select(a => a.Id));
            var authorized =
                (await _authorizationService.AuthorizeAsync(User, collections, BulkCollectionOperations.ModifyUserAccess))
                .Succeeded;
            if (!authorized)
            {
                throw new NotFoundException();
            }
        }

        var userId = _userService.GetProperUserId(User);
        await _organizationService.InviteUsersAsync(orgId, userId.Value, systemUser: null,
            [(new OrganizationUserInvite(model.ToData()), null)]);
    }

    [HttpPost("reinvite")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkReinvite(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        var userId = _userService.GetProperUserId(User);
        var result = await _organizationService.ResendInvitesAsync(orgId, userId.Value, model.Ids);
        return new ListResponseModel<OrganizationUserBulkResponseModel>(
            result.Select(t => new OrganizationUserBulkResponseModel(t.Item1.Id, t.Item2)));
    }

    [HttpPost("{id}/reinvite")]
    [Authorize<ManageUsersRequirement>]
    public async Task Reinvite(Guid orgId, Guid id)
    {
        var userId = _userService.GetProperUserId(User);
        await _organizationService.ResendInviteAsync(orgId, userId.Value, id);
    }

    [HttpPost("{organizationUserId}/accept-init")]
    public async Task AcceptInit(Guid orgId, Guid organizationUserId, [FromBody] OrganizationUserAcceptInitRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        await _initPendingOrganizationCommand.InitPendingOrganizationAsync(user, orgId, organizationUserId, model.Keys.PublicKey, model.Keys.EncryptedPrivateKey, model.CollectionName, model.Token);
        await _acceptOrgUserCommand.AcceptOrgUserByEmailTokenAsync(organizationUserId, user, model.Token, _userService);
        await _confirmOrganizationUserCommand.ConfirmUserAsync(orgId, organizationUserId, model.Key, user.Id);
    }

    [HttpPost("{organizationUserId}/accept")]
    public async Task Accept(Guid orgId, Guid organizationUserId, [FromBody] OrganizationUserAcceptRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var useMasterPasswordPolicy = _featureService.IsEnabled(FeatureFlagKeys.PolicyRequirements)
        ? (await _policyRequirementQuery.GetAsync<ResetPasswordPolicyRequirement>(user.Id)).AutoEnrollEnabled(orgId)
        : await ShouldHandleResetPasswordAsync(orgId);

        if (useMasterPasswordPolicy && string.IsNullOrWhiteSpace(model.ResetPasswordKey))
        {
            throw new BadRequestException("Master Password reset is required, but not provided.");
        }

        await _acceptOrgUserCommand.AcceptOrgUserByEmailTokenAsync(organizationUserId, user, model.Token, _userService);

        if (useMasterPasswordPolicy)
        {
            await _organizationService.UpdateUserResetPasswordEnrollmentAsync(orgId, user.Id, model.ResetPasswordKey, user.Id);
        }
    }

    private async Task<bool> ShouldHandleResetPasswordAsync(Guid orgId)
    {
        var organizationAbility = await _applicationCacheService.GetOrganizationAbilityAsync(orgId);

        if (organizationAbility is not { UsePolicies: true })
        {
            return false;
        }

        var masterPasswordPolicy = await _policyRepository.GetByOrganizationIdTypeAsync(orgId, PolicyType.ResetPassword);
        var useMasterPasswordPolicy = masterPasswordPolicy != null &&
                                          masterPasswordPolicy.Enabled &&
                                          masterPasswordPolicy.GetDataModel<ResetPasswordDataModel>().AutoEnrollEnabled;

        return useMasterPasswordPolicy;
    }

    [HttpPost("{id}/confirm")]
    [Authorize<ManageUsersRequirement>]
    public async Task Confirm(Guid orgId, Guid id, [FromBody] OrganizationUserConfirmRequestModel model)
    {
        var userId = _userService.GetProperUserId(User);
        _ = await _confirmOrganizationUserCommand.ConfirmUserAsync(orgId, id, model.Key, userId.Value, model.DefaultUserCollectionName);
    }

    [HttpPost("confirm")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkConfirm(Guid orgId,
        [FromBody] OrganizationUserBulkConfirmRequestModel model)
    {
        var userId = _userService.GetProperUserId(User);
        var results = await _confirmOrganizationUserCommand.ConfirmUsersAsync(orgId, model.ToDictionary(), userId.Value);

        return new ListResponseModel<OrganizationUserBulkResponseModel>(results.Select(r =>
            new OrganizationUserBulkResponseModel(r.Item1.Id, r.Item2)));
    }

    [HttpPost("public-keys")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserPublicKeyResponseModel>> UserPublicKeys(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        var result = await _organizationUserRepository.GetManyPublicKeysByOrganizationUserAsync(orgId, model.Ids);
        var responses = result.Select(r => new OrganizationUserPublicKeyResponseModel(r.Id, r.UserId, r.PublicKey)).ToList();
        return new ListResponseModel<OrganizationUserPublicKeyResponseModel>(responses);
    }

    [HttpPut("{id}")]
    [HttpPost("{id}")]
    [Authorize<ManageUsersRequirement>]
    public async Task Put(Guid orgId, Guid id, [FromBody] OrganizationUserUpdateRequestModel model)
    {
        var (organizationUser, currentAccess) = await _organizationUserRepository.GetByIdWithCollectionsAsync(id);
        if (organizationUser == null || organizationUser.OrganizationId != orgId)
        {
            throw new NotFoundException();
        }

        var userId = _userService.GetProperUserId(User).Value;
        var editingSelf = userId == organizationUser.UserId;

        // Authorization check:
        // If admins are not allowed access to all collections, you cannot add yourself to a group.
        // No error is thrown for this, we just don't update groups.
        var organizationAbility = await _applicationCacheService.GetOrganizationAbilityAsync(orgId);
        var groupsToSave = editingSelf && !organizationAbility.AllowAdminAccessToAllCollectionItems
            ? null
            : model.Groups;

        // Authorization check:
        // If admins are not allowed access to all collections, you cannot add yourself to collections.
        // This is not caught by the requirement below that you can ModifyUserAccess and must be checked separately
        var currentAccessIds = currentAccess.Select(c => c.Id).ToHashSet();
        if (editingSelf &&
            !organizationAbility.AllowAdminAccessToAllCollectionItems &&
            model.Collections.Any(c => !currentAccessIds.Contains(c.Id)))
        {
            throw new BadRequestException("You cannot add yourself to a collection.");
        }

        // Authorization check:
        // You must have authorization to ModifyUserAccess for all collections being saved
        var postedCollections = await _collectionRepository
            .GetManyByManyIdsAsync(model.Collections.Select(c => c.Id));
        foreach (var collection in postedCollections)
        {
            if (!(await _authorizationService.AuthorizeAsync(User, collection,
                    BulkCollectionOperations.ModifyUserAccess))
                .Succeeded)
            {
                throw new NotFoundException();
            }
        }

        // The client only sends collections that the saving user has permissions to edit.
        // We need to combine these with collections that the user doesn't have permissions for, so that we don't
        // accidentally overwrite those
        var currentCollections = await _collectionRepository
            .GetManyByManyIdsAsync(currentAccess.Select(cas => cas.Id));

        var readonlyCollectionIds = new HashSet<Guid>();
        foreach (var collection in currentCollections)
        {
            if (!(await _authorizationService.AuthorizeAsync(User, collection, BulkCollectionOperations.ModifyUserAccess))
                .Succeeded)
            {
                readonlyCollectionIds.Add(collection.Id);
            }
        }

        var editedCollectionAccess = model.Collections
            .Select(c => c.ToSelectionReadOnly());
        var readonlyCollectionAccess = currentAccess
            .Where(ca => readonlyCollectionIds.Contains(ca.Id));
        var collectionsToSave = editedCollectionAccess
            .Concat(readonlyCollectionAccess)
            .ToList();

        var existingUserType = organizationUser.Type;

        await _updateOrganizationUserCommand.UpdateUserAsync(model.ToOrganizationUser(organizationUser), existingUserType, userId,
            collectionsToSave, groupsToSave);
    }

    [HttpPut("{userId}/reset-password-enrollment")]
    public async Task PutResetPasswordEnrollment(Guid orgId, Guid userId, [FromBody] OrganizationUserResetPasswordEnrollmentRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var ssoConfig = await _ssoConfigRepository.GetByOrganizationIdAsync(orgId);
        var isTdeEnrollment = ssoConfig != null && ssoConfig.Enabled && ssoConfig.GetData().MemberDecryptionType == MemberDecryptionType.TrustedDeviceEncryption;
        if (!isTdeEnrollment && !string.IsNullOrWhiteSpace(model.ResetPasswordKey) && !await _userService.VerifySecretAsync(user, model.MasterPasswordHash))
        {
            throw new BadRequestException("Incorrect password");
        }

        var callingUserId = user.Id;
        await _organizationService.UpdateUserResetPasswordEnrollmentAsync(
            orgId, userId, model.ResetPasswordKey, callingUserId);

        var orgUser = await _organizationUserRepository.GetByOrganizationAsync(orgId, user.Id);
        if (orgUser.Status == OrganizationUserStatusType.Invited)
        {
            await _acceptOrgUserCommand.AcceptOrgUserByOrgIdAsync(orgId, user, _userService);
        }
    }

    [HttpPut("{id}/reset-password")]
    [Authorize<ManageAccountRecoveryRequirement>]
    public async Task PutResetPassword(Guid orgId, Guid id, [FromBody] OrganizationUserResetPasswordRequestModel model)
    {
        // Get the users role, since provider users aren't a member of the organization we use the owner check
        var orgUserType = await _currentContext.OrganizationOwner(orgId)
            ? OrganizationUserType.Owner
            : _currentContext.Organizations?.FirstOrDefault(o => o.Id == orgId)?.Type;
        if (orgUserType == null)
        {
            throw new NotFoundException();
        }

        var result = await _userService.AdminResetPasswordAsync(orgUserType.Value, orgId, id, model.NewMasterPasswordHash, model.Key);
        if (result.Succeeded)
        {
            return;
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        await Task.Delay(2000);
        throw new BadRequestException(ModelState);
    }

    [HttpDelete("{id}")]
    [HttpPost("{id}/remove")]
    [Authorize<ManageUsersRequirement>]
    public async Task Remove(Guid orgId, Guid id)
    {
        var userId = _userService.GetProperUserId(User);
        await _removeOrganizationUserCommand.RemoveUserAsync(orgId, id, userId.Value);
    }

    [HttpDelete("")]
    [HttpPost("remove")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkRemove(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        var userId = _userService.GetProperUserId(User);
        var result = await _removeOrganizationUserCommand.RemoveUsersAsync(orgId, model.Ids, userId.Value);
        return new ListResponseModel<OrganizationUserBulkResponseModel>(result.Select(r =>
            new OrganizationUserBulkResponseModel(r.OrganizationUserId, r.ErrorMessage)));
    }

    [HttpDelete("{id}/delete-account")]
    [HttpPost("{id}/delete-account")]
    [Authorize<ManageUsersRequirement>]
    public async Task DeleteAccount(Guid orgId, Guid id)
    {
        var currentUser = await _userService.GetUserByPrincipalAsync(User);
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException();
        }

        await _deleteClaimedOrganizationUserAccountCommand.DeleteUserAsync(orgId, id, currentUser.Id);
    }

    [HttpDelete("delete-account")]
    [HttpPost("delete-account")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkDeleteAccount(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        var currentUser = await _userService.GetUserByPrincipalAsync(User);
        if (currentUser == null)
        {
            throw new UnauthorizedAccessException();
        }

        var results = await _deleteClaimedOrganizationUserAccountCommand.DeleteManyUsersAsync(orgId, model.Ids, currentUser.Id);

        return new ListResponseModel<OrganizationUserBulkResponseModel>(results.Select(r =>
            new OrganizationUserBulkResponseModel(r.OrganizationUserId, r.ErrorMessage)));
    }

    [HttpPatch("{id}/revoke")]
    [HttpPut("{id}/revoke")]
    [Authorize<ManageUsersRequirement>]
    public async Task RevokeAsync(Guid orgId, Guid id)
    {
        await RestoreOrRevokeUserAsync(orgId, id, _revokeOrganizationUserCommand.RevokeUserAsync);
    }

    [HttpPatch("revoke")]
    [HttpPut("revoke")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkRevokeAsync(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        return await RestoreOrRevokeUsersAsync(orgId, model, _revokeOrganizationUserCommand.RevokeUsersAsync);
    }

    [HttpPatch("{id}/restore")]
    [HttpPut("{id}/restore")]
    [Authorize<ManageUsersRequirement>]
    public async Task RestoreAsync(Guid orgId, Guid id)
    {
        await RestoreOrRevokeUserAsync(orgId, id, (orgUser, userId) => _restoreOrganizationUserCommand.RestoreUserAsync(orgUser, userId));
    }

    [HttpPatch("restore")]
    [HttpPut("restore")]
    [Authorize<ManageUsersRequirement>]
    public async Task<ListResponseModel<OrganizationUserBulkResponseModel>> BulkRestoreAsync(Guid orgId, [FromBody] OrganizationUserBulkRequestModel model)
    {
        return await RestoreOrRevokeUsersAsync(orgId, model, (orgId, orgUserIds, restoringUserId) => _restoreOrganizationUserCommand.RestoreUsersAsync(orgId, orgUserIds, restoringUserId, _userService));
    }

    [HttpPatch("enable-secrets-manager")]
    [HttpPut("enable-secrets-manager")]
    [Authorize<ManageUsersRequirement>]
    public async Task BulkEnableSecretsManagerAsync(Guid orgId,
        [FromBody] OrganizationUserBulkRequestModel model)
    {
        var orgUsers = (await _organizationUserRepository.GetManyAsync(model.Ids))
            .Where(ou => ou.OrganizationId == orgId && !ou.AccessSecretsManager).ToList();
        if (orgUsers.Count == 0)
        {
            throw new BadRequestException("Users invalid.");
        }

        var additionalSmSeatsRequired = await _countNewSmSeatsRequiredQuery.CountNewSmSeatsRequiredAsync(orgId,
            orgUsers.Count);
        if (additionalSmSeatsRequired > 0)
        {
            var organization = await _organizationRepository.GetByIdAsync(orgId);
            // TODO: https://bitwarden.atlassian.net/browse/PM-17000
            var plan = await _pricingClient.GetPlanOrThrow(organization!.PlanType);
            var update = new SecretsManagerSubscriptionUpdate(organization, plan, true)
                .AdjustSeats(additionalSmSeatsRequired);
            await _updateSecretsManagerSubscriptionCommand.UpdateSubscriptionAsync(update);
        }

        foreach (var orgUser in orgUsers)
        {
            orgUser.AccessSecretsManager = true;
        }

        await _organizationUserRepository.ReplaceManyAsync(orgUsers);
    }

    private async Task RestoreOrRevokeUserAsync(
        Guid orgId,
        Guid id,
        Func<Core.Entities.OrganizationUser, Guid?, Task> statusAction)
    {
        var userId = _userService.GetProperUserId(User);
        var orgUser = await _organizationUserRepository.GetByIdAsync(id);
        if (orgUser == null || orgUser.OrganizationId != orgId)
        {
            throw new NotFoundException();
        }

        await statusAction(orgUser, userId);
    }

    private async Task<ListResponseModel<OrganizationUserBulkResponseModel>> RestoreOrRevokeUsersAsync(
        Guid orgId,
        OrganizationUserBulkRequestModel model,
        Func<Guid, IEnumerable<Guid>, Guid?, Task<List<Tuple<Core.Entities.OrganizationUser, string>>>> statusAction)
    {
        var userId = _userService.GetProperUserId(User);
        var result = await statusAction(orgId, model.Ids, userId.Value);
        return new ListResponseModel<OrganizationUserBulkResponseModel>(result.Select(r =>
            new OrganizationUserBulkResponseModel(r.Item1.Id, r.Item2)));
    }

    private async Task<IDictionary<Guid, bool>> GetClaimedByOrganizationStatusAsync(Guid orgId, IEnumerable<Guid> userIds)
    {
        var usersOrganizationClaimedStatus = await _getOrganizationUsersClaimedStatusQuery.GetUsersOrganizationClaimedStatusAsync(orgId, userIds);
        return usersOrganizationClaimedStatus;
    }
}
