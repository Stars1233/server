﻿using Bit.Api.AdminConsole.Models.Request.Organizations;
using Bit.Api.AdminConsole.Models.Response;
using Bit.Api.Auth.Models.Request;
using Bit.Api.Auth.Models.Request.Accounts;
using Bit.Api.Auth.Models.Request.WebAuthn;
using Bit.Api.KeyManagement.Validators;
using Bit.Api.Models.Request.Accounts;
using Bit.Api.Models.Response;
using Bit.Api.Tools.Models.Request;
using Bit.Api.Vault.Models.Request;
using Bit.Core;
using Bit.Core.AdminConsole.Enums.Provider;
using Bit.Core.AdminConsole.Repositories;
using Bit.Core.AdminConsole.Services;
using Bit.Core.Auth.Entities;
using Bit.Core.Auth.Models.Api.Request.Accounts;
using Bit.Core.Auth.Models.Data;
using Bit.Core.Auth.UserFeatures.TdeOffboardingPassword.Interfaces;
using Bit.Core.Auth.UserFeatures.TwoFactorAuth.Interfaces;
using Bit.Core.Auth.UserFeatures.UserMasterPassword.Interfaces;
using Bit.Core.Entities;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.KeyManagement.Models.Data;
using Bit.Core.KeyManagement.UserKey;
using Bit.Core.Models.Api.Response;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Tools.Entities;
using Bit.Core.Utilities;
using Bit.Core.Vault.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bit.Api.Auth.Controllers;

[Route("accounts")]
[Authorize("Application")]
public class AccountsController : Controller
{
    private readonly IOrganizationService _organizationService;
    private readonly IOrganizationUserRepository _organizationUserRepository;
    private readonly IProviderUserRepository _providerUserRepository;
    private readonly IUserService _userService;
    private readonly IPolicyService _policyService;
    private readonly ISetInitialMasterPasswordCommand _setInitialMasterPasswordCommand;
    private readonly ITdeOffboardingPasswordCommand _tdeOffboardingPasswordCommand;
    private readonly IRotateUserKeyCommand _rotateUserKeyCommand;
    private readonly ITwoFactorIsEnabledQuery _twoFactorIsEnabledQuery;
    private readonly IFeatureService _featureService;

    private readonly IRotationValidator<IEnumerable<CipherWithIdRequestModel>, IEnumerable<Cipher>> _cipherValidator;
    private readonly IRotationValidator<IEnumerable<FolderWithIdRequestModel>, IEnumerable<Folder>> _folderValidator;
    private readonly IRotationValidator<IEnumerable<SendWithIdRequestModel>, IReadOnlyList<Send>> _sendValidator;
    private readonly IRotationValidator<IEnumerable<EmergencyAccessWithIdRequestModel>, IEnumerable<EmergencyAccess>>
        _emergencyAccessValidator;
    private readonly IRotationValidator<IEnumerable<ResetPasswordWithOrgIdRequestModel>,
            IReadOnlyList<OrganizationUser>>
        _organizationUserValidator;
    private readonly IRotationValidator<IEnumerable<WebAuthnLoginRotateKeyRequestModel>, IEnumerable<WebAuthnLoginRotateKeyData>>
        _webauthnKeyValidator;


    public AccountsController(
        IOrganizationService organizationService,
        IOrganizationUserRepository organizationUserRepository,
        IProviderUserRepository providerUserRepository,
        IUserService userService,
        IPolicyService policyService,
        ISetInitialMasterPasswordCommand setInitialMasterPasswordCommand,
        ITdeOffboardingPasswordCommand tdeOffboardingPasswordCommand,
        IRotateUserKeyCommand rotateUserKeyCommand,
        ITwoFactorIsEnabledQuery twoFactorIsEnabledQuery,
        IFeatureService featureService,
        IRotationValidator<IEnumerable<CipherWithIdRequestModel>, IEnumerable<Cipher>> cipherValidator,
        IRotationValidator<IEnumerable<FolderWithIdRequestModel>, IEnumerable<Folder>> folderValidator,
        IRotationValidator<IEnumerable<SendWithIdRequestModel>, IReadOnlyList<Send>> sendValidator,
        IRotationValidator<IEnumerable<EmergencyAccessWithIdRequestModel>, IEnumerable<EmergencyAccess>>
            emergencyAccessValidator,
        IRotationValidator<IEnumerable<ResetPasswordWithOrgIdRequestModel>, IReadOnlyList<OrganizationUser>>
            organizationUserValidator,
        IRotationValidator<IEnumerable<WebAuthnLoginRotateKeyRequestModel>, IEnumerable<WebAuthnLoginRotateKeyData>> webAuthnKeyValidator
        )
    {
        _organizationService = organizationService;
        _organizationUserRepository = organizationUserRepository;
        _providerUserRepository = providerUserRepository;
        _userService = userService;
        _policyService = policyService;
        _setInitialMasterPasswordCommand = setInitialMasterPasswordCommand;
        _tdeOffboardingPasswordCommand = tdeOffboardingPasswordCommand;
        _rotateUserKeyCommand = rotateUserKeyCommand;
        _twoFactorIsEnabledQuery = twoFactorIsEnabledQuery;
        _featureService = featureService;
        _cipherValidator = cipherValidator;
        _folderValidator = folderValidator;
        _sendValidator = sendValidator;
        _emergencyAccessValidator = emergencyAccessValidator;
        _organizationUserValidator = organizationUserValidator;
        _webauthnKeyValidator = webAuthnKeyValidator;
    }


    [HttpPost("password-hint")]
    [AllowAnonymous]
    public async Task PostPasswordHint([FromBody] PasswordHintRequestModel model)
    {
        await _userService.SendMasterPasswordHintAsync(model.Email);
    }

    [HttpPost("email-token")]
    public async Task PostEmailToken([FromBody] EmailTokenRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (user.UsesKeyConnector)
        {
            throw new BadRequestException("You cannot change your email when using Key Connector.");
        }

        if (!await _userService.CheckPasswordAsync(user, model.MasterPasswordHash))
        {
            await Task.Delay(2000);
            throw new BadRequestException("MasterPasswordHash", "Invalid password.");
        }

        var claimedUserValidationResult = await _userService.ValidateClaimedUserDomainAsync(user, model.NewEmail);

        if (!claimedUserValidationResult.Succeeded)
        {
            throw new BadRequestException(claimedUserValidationResult.Errors);
        }

        await _userService.InitiateEmailChangeAsync(user, model.NewEmail);
    }

    [HttpPost("email")]
    public async Task PostEmail([FromBody] EmailRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (user.UsesKeyConnector)
        {
            throw new BadRequestException("You cannot change your email when using Key Connector.");
        }

        var result = await _userService.ChangeEmailAsync(user, model.MasterPasswordHash, model.NewEmail,
            model.NewMasterPasswordHash, model.Token, model.Key);
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

    [HttpPost("verify-email")]
    public async Task PostVerifyEmail()
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        await _userService.SendEmailVerificationAsync(user);
    }

    [HttpPost("verify-email-token")]
    [AllowAnonymous]
    public async Task PostVerifyEmailToken([FromBody] VerifyEmailRequestModel model)
    {
        var user = await _userService.GetUserByIdAsync(new Guid(model.UserId));
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }
        var result = await _userService.ConfirmEmailAsync(user, model.Token);
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

    [HttpPost("password")]
    public async Task PostPassword([FromBody] PasswordRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _userService.ChangePasswordAsync(user, model.MasterPasswordHash,
            model.NewMasterPasswordHash, model.MasterPasswordHint, model.Key);
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

    [HttpPost("set-password")]
    public async Task PostSetPasswordAsync([FromBody] SetPasswordRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        try
        {
            user = model.ToUser(user);
        }
        catch (Exception e)
        {
            ModelState.AddModelError(string.Empty, e.Message);
            throw new BadRequestException(ModelState);
        }

        var result = await _setInitialMasterPasswordCommand.SetInitialMasterPasswordAsync(
            user,
            model.MasterPasswordHash,
            model.Key,
            model.OrgIdentifier);

        if (result.Succeeded)
        {
            return;
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        throw new BadRequestException(ModelState);
    }

    [HttpPost("verify-password")]
    public async Task<MasterPasswordPolicyResponseModel> PostVerifyPassword([FromBody] SecretVerificationRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (await _userService.CheckPasswordAsync(user, model.MasterPasswordHash))
        {
            var policyData = await _policyService.GetMasterPasswordPolicyForUserAsync(user);

            return new MasterPasswordPolicyResponseModel(policyData);
        }

        ModelState.AddModelError(nameof(model.MasterPasswordHash), "Invalid password.");
        await Task.Delay(2000);
        throw new BadRequestException(ModelState);
    }

    [HttpPost("kdf")]
    public async Task PostKdf([FromBody] KdfRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _userService.ChangeKdfAsync(user, model.MasterPasswordHash,
            model.NewMasterPasswordHash, model.Key, model.Kdf.Value, model.KdfIterations.Value, model.KdfMemory, model.KdfParallelism);
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

    [Obsolete("Replaced by the safer rotate-user-account-keys endpoint.")]
    [HttpPost("key")]
    public async Task PostKey([FromBody] UpdateKeyRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var dataModel = new RotateUserKeyData
        {
            MasterPasswordHash = model.MasterPasswordHash,
            Key = model.Key,
            PrivateKey = model.PrivateKey,
            Ciphers = await _cipherValidator.ValidateAsync(user, model.Ciphers),
            Folders = await _folderValidator.ValidateAsync(user, model.Folders),
            Sends = await _sendValidator.ValidateAsync(user, model.Sends),
            EmergencyAccesses = await _emergencyAccessValidator.ValidateAsync(user, model.EmergencyAccessKeys),
            OrganizationUsers = await _organizationUserValidator.ValidateAsync(user, model.ResetPasswordKeys),
            WebAuthnKeys = await _webauthnKeyValidator.ValidateAsync(user, model.WebAuthnKeys)
        };

        var result = await _rotateUserKeyCommand.RotateUserKeyAsync(user, dataModel);

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

    [HttpPost("security-stamp")]
    public async Task PostSecurityStamp([FromBody] SecretVerificationRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _userService.RefreshSecurityStampAsync(user, model.Secret);
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

    [HttpGet("profile")]
    public async Task<ProfileResponseModel> GetProfile()
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var organizationUserDetails = await _organizationUserRepository.GetManyDetailsByUserAsync(user.Id,
            OrganizationUserStatusType.Confirmed);
        var providerUserDetails = await _providerUserRepository.GetManyDetailsByUserAsync(user.Id,
            ProviderUserStatusType.Confirmed);
        var providerUserOrganizationDetails =
            await _providerUserRepository.GetManyOrganizationDetailsByUserAsync(user.Id,
                ProviderUserStatusType.Confirmed);

        var twoFactorEnabled = await _twoFactorIsEnabledQuery.TwoFactorIsEnabledAsync(user);
        var hasPremiumFromOrg = await _userService.HasPremiumFromOrganization(user);
        var organizationIdsClaimingActiveUser = await GetOrganizationIdsClaimingUserAsync(user.Id);

        var response = new ProfileResponseModel(user, organizationUserDetails, providerUserDetails,
            providerUserOrganizationDetails, twoFactorEnabled,
            hasPremiumFromOrg, organizationIdsClaimingActiveUser);
        return response;
    }

    [HttpGet("organizations")]
    public async Task<ListResponseModel<ProfileOrganizationResponseModel>> GetOrganizations()
    {
        var userId = _userService.GetProperUserId(User);
        var organizationUserDetails = await _organizationUserRepository.GetManyDetailsByUserAsync(userId.Value,
            OrganizationUserStatusType.Confirmed);
        var organizationIdsClaimingUser = await GetOrganizationIdsClaimingUserAsync(userId.Value);

        var responseData = organizationUserDetails.Select(o => new ProfileOrganizationResponseModel(o, organizationIdsClaimingUser));
        return new ListResponseModel<ProfileOrganizationResponseModel>(responseData);
    }

    [HttpPut("profile")]
    [HttpPost("profile")]
    public async Task<ProfileResponseModel> PutProfile([FromBody] UpdateProfileRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        await _userService.SaveUserAsync(model.ToUser(user));

        var twoFactorEnabled = await _twoFactorIsEnabledQuery.TwoFactorIsEnabledAsync(user);
        var hasPremiumFromOrg = await _userService.HasPremiumFromOrganization(user);
        var organizationIdsClaimingActiveUser = await GetOrganizationIdsClaimingUserAsync(user.Id);

        var response = new ProfileResponseModel(user, null, null, null, twoFactorEnabled, hasPremiumFromOrg, organizationIdsClaimingActiveUser);
        return response;
    }

    [HttpPut("avatar")]
    [HttpPost("avatar")]
    public async Task<ProfileResponseModel> PutAvatar([FromBody] UpdateAvatarRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }
        await _userService.SaveUserAsync(model.ToUser(user), true);

        var userTwoFactorEnabled = await _twoFactorIsEnabledQuery.TwoFactorIsEnabledAsync(user);
        var userHasPremiumFromOrganization = await _userService.HasPremiumFromOrganization(user);
        var organizationIdsClaimingActiveUser = await GetOrganizationIdsClaimingUserAsync(user.Id);

        var response = new ProfileResponseModel(user, null, null, null, userTwoFactorEnabled, userHasPremiumFromOrganization, organizationIdsClaimingActiveUser);
        return response;
    }

    [HttpGet("revision-date")]
    public async Task<long?> GetAccountRevisionDate()
    {
        var userId = _userService.GetProperUserId(User);
        long? revisionDate = null;
        if (userId.HasValue)
        {
            var date = await _userService.GetAccountRevisionDateByIdAsync(userId.Value);
            revisionDate = CoreHelpers.ToEpocMilliseconds(date);
        }

        return revisionDate;
    }

    [HttpPost("keys")]
    public async Task<KeysResponseModel> PostKeys([FromBody] KeysRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (_featureService.IsEnabled(FeatureFlagKeys.ReturnErrorOnExistingKeypair))
        {
            if (!string.IsNullOrWhiteSpace(user.PrivateKey) || !string.IsNullOrWhiteSpace(user.PublicKey))
            {
                throw new BadRequestException("User has existing keypair");
            }
        }

        await _userService.SaveUserAsync(model.ToUser(user));
        return new KeysResponseModel(user);
    }

    [HttpGet("keys")]
    public async Task<KeysResponseModel> GetKeys()
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        return new KeysResponseModel(user);
    }

    [HttpDelete]
    [HttpPost("delete")]
    public async Task Delete([FromBody] SecretVerificationRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (!await _userService.VerifySecretAsync(user, model.Secret))
        {
            ModelState.AddModelError(string.Empty, "User verification failed.");
            await Task.Delay(2000);
        }
        else
        {
            // Check if the user is claimed by any organization.
            if (await _userService.IsClaimedByAnyOrganizationAsync(user.Id))
            {
                throw new BadRequestException("Cannot delete accounts owned by an organization. Contact your organization administrator for additional details.");
            }

            var result = await _userService.DeleteAsync(user);
            if (result.Succeeded)
            {
                return;
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
        }

        throw new BadRequestException(ModelState);
    }

    [AllowAnonymous]
    [HttpPost("delete-recover")]
    public async Task PostDeleteRecover([FromBody] DeleteRecoverRequestModel model)
    {
        await _userService.SendDeleteConfirmationAsync(model.Email);
    }

    [HttpPost("delete-recover-token")]
    [AllowAnonymous]
    public async Task PostDeleteRecoverToken([FromBody] VerifyDeleteRecoverRequestModel model)
    {
        var user = await _userService.GetUserByIdAsync(new Guid(model.UserId));
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _userService.DeleteAsync(user, model.Token);
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

    [HttpDelete("sso/{organizationId}")]
    public async Task DeleteSsoUser(string organizationId)
    {
        var userId = _userService.GetProperUserId(User);
        if (!userId.HasValue)
        {
            throw new NotFoundException();
        }

        await _organizationService.DeleteSsoUserAsync(userId.Value, new Guid(organizationId));
    }

    [HttpGet("sso/user-identifier")]
    public async Task<string> GetSsoUserIdentifier()
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        var token = await _userService.GenerateSignInTokenAsync(user, TokenPurposes.LinkSso);
        var userIdentifier = $"{user.Id},{token}";
        return userIdentifier;
    }

    [HttpPost("api-key")]
    public async Task<ApiKeyResponseModel> ApiKey([FromBody] SecretVerificationRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (!await _userService.VerifySecretAsync(user, model.Secret))
        {
            await Task.Delay(2000);
            throw new BadRequestException(string.Empty, "User verification failed.");
        }

        return new ApiKeyResponseModel(user);
    }

    [HttpPost("rotate-api-key")]
    public async Task<ApiKeyResponseModel> RotateApiKey([FromBody] SecretVerificationRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        if (!await _userService.VerifySecretAsync(user, model.Secret))
        {
            await Task.Delay(2000);
            throw new BadRequestException(string.Empty, "User verification failed.");
        }

        await _userService.RotateApiKeyAsync(user);
        var response = new ApiKeyResponseModel(user);
        return response;
    }

    [HttpPut("update-temp-password")]
    public async Task PutUpdateTempPasswordAsync([FromBody] UpdateTempPasswordRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _userService.UpdateTempPasswordAsync(user, model.NewMasterPasswordHash, model.Key, model.MasterPasswordHint);
        if (result.Succeeded)
        {
            return;
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        throw new BadRequestException(ModelState);
    }

    [HttpPut("update-tde-offboarding-password")]
    public async Task PutUpdateTdePasswordAsync([FromBody] UpdateTdeOffboardingPasswordRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);
        if (user == null)
        {
            throw new UnauthorizedAccessException();
        }

        var result = await _tdeOffboardingPasswordCommand.UpdateTdeOffboardingPasswordAsync(user, model.NewMasterPasswordHash, model.Key, model.MasterPasswordHint);
        if (result.Succeeded)
        {
            return;
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        throw new BadRequestException(ModelState);
    }

    [HttpPost("request-otp")]
    public async Task PostRequestOTP()
    {
        var user = await _userService.GetUserByPrincipalAsync(User);

        await _userService.SendOTPAsync(user);
    }

    [HttpPost("verify-otp")]
    public async Task VerifyOTP([FromBody] VerifyOTPRequestModel model)
    {
        var user = await _userService.GetUserByPrincipalAsync(User);

        if (!await _userService.VerifyOTPAsync(user, model.OTP))
        {
            await Task.Delay(2000);
            throw new BadRequestException("Token", "Invalid token");
        }
    }

    [AllowAnonymous]
    [HttpPost("resend-new-device-otp")]
    public async Task ResendNewDeviceOtpAsync([FromBody] UnauthenticatedSecretVerificationRequestModel request)
    {
        await _userService.ResendNewDeviceVerificationEmail(request.Email, request.Secret);
    }

    [HttpPost("verify-devices")]
    [HttpPut("verify-devices")]
    public async Task SetUserVerifyDevicesAsync([FromBody] SetVerifyDevicesRequestModel request)
    {
        var user = await _userService.GetUserByPrincipalAsync(User) ?? throw new UnauthorizedAccessException();

        if (!await _userService.VerifySecretAsync(user, request.Secret))
        {
            await Task.Delay(2000);
            throw new BadRequestException(string.Empty, "User verification failed.");
        }
        user.VerifyDevices = request.VerifyDevices;

        await _userService.SaveUserAsync(user);
    }

    private async Task<IEnumerable<Guid>> GetOrganizationIdsClaimingUserAsync(Guid userId)
    {
        var organizationsClaimingUser = await _userService.GetOrganizationsClaimingUserAsync(userId);
        return organizationsClaimingUser.Select(o => o.Id);
    }
}
