﻿// FIXME: Update this file to be null safe and then delete the line below
#nullable disable

using System.Text.Json;
using Azure.Messaging.EventGrid;
using Bit.Api.Models.Response;
using Bit.Api.Tools.Models.Request;
using Bit.Api.Tools.Models.Response;
using Bit.Api.Utilities;
using Bit.Core;
using Bit.Core.Exceptions;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Tools.Enums;
using Bit.Core.Tools.Models.Data;
using Bit.Core.Tools.Repositories;
using Bit.Core.Tools.SendFeatures;
using Bit.Core.Tools.SendFeatures.Commands.Interfaces;
using Bit.Core.Tools.Services;
using Bit.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bit.Api.Tools.Controllers;

[Route("sends")]
[Authorize("Application")]
public class SendsController : Controller
{
    private readonly ISendRepository _sendRepository;
    private readonly IUserService _userService;
    private readonly ISendAuthorizationService _sendAuthorizationService;
    private readonly ISendFileStorageService _sendFileStorageService;
    private readonly IAnonymousSendCommand _anonymousSendCommand;
    private readonly INonAnonymousSendCommand _nonAnonymousSendCommand;
    private readonly ILogger<SendsController> _logger;
    private readonly GlobalSettings _globalSettings;

    public SendsController(
        ISendRepository sendRepository,
        IUserService userService,
        ISendAuthorizationService sendAuthorizationService,
        IAnonymousSendCommand anonymousSendCommand,
        INonAnonymousSendCommand nonAnonymousSendCommand,
        ISendFileStorageService sendFileStorageService,
        ILogger<SendsController> logger,
        GlobalSettings globalSettings)
    {
        _sendRepository = sendRepository;
        _userService = userService;
        _sendAuthorizationService = sendAuthorizationService;
        _anonymousSendCommand = anonymousSendCommand;
        _nonAnonymousSendCommand = nonAnonymousSendCommand;
        _sendFileStorageService = sendFileStorageService;
        _logger = logger;
        _globalSettings = globalSettings;
    }

    #region Anonymous endpoints
    [AllowAnonymous]
    [HttpPost("access/{id}")]
    public async Task<IActionResult> Access(string id, [FromBody] SendAccessRequestModel model)
    {
        // Uncomment whenever we want to require the `send-id` header
        //if (!_currentContext.HttpContext.Request.Headers.ContainsKey("Send-Id") ||
        //    _currentContext.HttpContext.Request.Headers["Send-Id"] != id)
        //{
        //    throw new BadRequestException("Invalid Send-Id header.");
        //}

        var guid = new Guid(CoreHelpers.Base64UrlDecode(id));
        var send = await _sendRepository.GetByIdAsync(guid);
        SendAccessResult sendAuthResult =
            await _sendAuthorizationService.AccessAsync(send, model.Password);
        if (sendAuthResult.Equals(SendAccessResult.PasswordRequired))
        {
            return new UnauthorizedResult();
        }
        if (sendAuthResult.Equals(SendAccessResult.PasswordInvalid))
        {
            await Task.Delay(2000);
            throw new BadRequestException("Invalid password.");
        }
        if (sendAuthResult.Equals(SendAccessResult.Denied))
        {
            throw new NotFoundException();
        }

        var sendResponse = new SendAccessResponseModel(send, _globalSettings);
        if (send.UserId.HasValue && !send.HideEmail.GetValueOrDefault())
        {
            var creator = await _userService.GetUserByIdAsync(send.UserId.Value);
            sendResponse.CreatorIdentifier = creator.Email;
        }
        return new ObjectResult(sendResponse);
    }

    [AllowAnonymous]
    [HttpPost("{encodedSendId}/access/file/{fileId}")]
    public async Task<IActionResult> GetSendFileDownloadData(string encodedSendId,
        string fileId, [FromBody] SendAccessRequestModel model)
    {
        // Uncomment whenever we want to require the `send-id` header
        //if (!_currentContext.HttpContext.Request.Headers.ContainsKey("Send-Id") ||
        //    _currentContext.HttpContext.Request.Headers["Send-Id"] != encodedSendId)
        //{
        //    throw new BadRequestException("Invalid Send-Id header.");
        //}

        var sendId = new Guid(CoreHelpers.Base64UrlDecode(encodedSendId));
        var send = await _sendRepository.GetByIdAsync(sendId);

        if (send == null)
        {
            throw new BadRequestException("Could not locate send");
        }

        var (url, result) = await _anonymousSendCommand.GetSendFileDownloadUrlAsync(send, fileId,
            model.Password);

        if (result.Equals(SendAccessResult.PasswordRequired))
        {
            return new UnauthorizedResult();
        }
        if (result.Equals(SendAccessResult.PasswordInvalid))
        {
            await Task.Delay(2000);
            throw new BadRequestException("Invalid password.");
        }
        if (result.Equals(SendAccessResult.Denied))
        {
            throw new NotFoundException();
        }

        return new ObjectResult(new SendFileDownloadDataResponseModel()
        {
            Id = fileId,
            Url = url,
        });
    }

    [AllowAnonymous]
    [HttpPost("file/validate/azure")]
    public async Task<ObjectResult> AzureValidateFile()
    {
        return await ApiHelpers.HandleAzureEvents(Request, new Dictionary<string, Func<EventGridEvent, Task>>
        {
            {
                "Microsoft.Storage.BlobCreated", async (eventGridEvent) =>
                {
                    try
                    {
                        var blobName = eventGridEvent.Subject.Split($"{AzureSendFileStorageService.FilesContainerName}/blobs/")[1];
                        var sendId = AzureSendFileStorageService.SendIdFromBlobName(blobName);
                        var send = await _sendRepository.GetByIdAsync(new Guid(sendId));
                        if (send == null)
                        {
                            if (_sendFileStorageService is AzureSendFileStorageService azureSendFileStorageService)
                            {
                                await azureSendFileStorageService.DeleteBlobAsync(blobName);
                            }
                            return;
                        }

                        await _nonAnonymousSendCommand.ConfirmFileSize(send);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, $"Uncaught exception occurred while handling event grid event: {JsonSerializer.Serialize(eventGridEvent)}");
                        return;
                    }
                }
            }
        });
    }

    #endregion

    #region Non-anonymous endpoints

    [HttpGet("{id}")]
    public async Task<SendResponseModel> Get(string id)
    {
        var userId = _userService.GetProperUserId(User).Value;
        var send = await _sendRepository.GetByIdAsync(new Guid(id));
        if (send == null || send.UserId != userId)
        {
            throw new NotFoundException();
        }

        return new SendResponseModel(send, _globalSettings);
    }

    [HttpGet("")]
    public async Task<ListResponseModel<SendResponseModel>> Get()
    {
        var userId = _userService.GetProperUserId(User).Value;
        var sends = await _sendRepository.GetManyByUserIdAsync(userId);
        var responses = sends.Select(s => new SendResponseModel(s, _globalSettings));
        return new ListResponseModel<SendResponseModel>(responses);
    }

    [HttpPost("")]
    public async Task<SendResponseModel> Post([FromBody] SendRequestModel model)
    {
        model.ValidateCreation();
        var userId = _userService.GetProperUserId(User).Value;
        var send = model.ToSend(userId, _sendAuthorizationService);
        await _nonAnonymousSendCommand.SaveSendAsync(send);
        return new SendResponseModel(send, _globalSettings);
    }

    [HttpPost("file/v2")]
    public async Task<SendFileUploadDataResponseModel> PostFile([FromBody] SendRequestModel model)
    {
        if (model.Type != SendType.File)
        {
            throw new BadRequestException("Invalid content.");
        }

        if (!model.FileLength.HasValue)
        {
            throw new BadRequestException("Invalid content. File size hint is required.");
        }

        if (model.FileLength.Value > Constants.FileSize501mb)
        {
            throw new BadRequestException($"Max file size is {SendFileSettingHelper.MAX_FILE_SIZE_READABLE}.");
        }

        model.ValidateCreation();
        var userId = _userService.GetProperUserId(User).Value;
        var (send, data) = model.ToSend(userId, model.File.FileName, _sendAuthorizationService);
        var uploadUrl = await _nonAnonymousSendCommand.SaveFileSendAsync(send, data, model.FileLength.Value);
        return new SendFileUploadDataResponseModel
        {
            Url = uploadUrl,
            FileUploadType = _sendFileStorageService.FileUploadType,
            SendResponse = new SendResponseModel(send, _globalSettings)
        };
    }

    [HttpGet("{id}/file/{fileId}")]
    public async Task<SendFileUploadDataResponseModel> RenewFileUpload(string id, string fileId)
    {
        var userId = _userService.GetProperUserId(User).Value;
        var sendId = new Guid(id);
        var send = await _sendRepository.GetByIdAsync(sendId);
        var fileData = JsonSerializer.Deserialize<SendFileData>(send?.Data);

        if (send == null || send.Type != SendType.File || (send.UserId.HasValue && send.UserId.Value != userId) ||
            !send.UserId.HasValue || fileData.Id != fileId || fileData.Validated)
        {
            // Not found if Send isn't found, user doesn't have access, request is faulty,
            // or we've already validated the file. This last is to emulate create-only blob permissions for Azure
            throw new NotFoundException();
        }

        return new SendFileUploadDataResponseModel
        {
            Url = await _sendFileStorageService.GetSendFileUploadUrlAsync(send, fileId),
            FileUploadType = _sendFileStorageService.FileUploadType,
            SendResponse = new SendResponseModel(send, _globalSettings),
        };
    }

    [HttpPost("{id}/file/{fileId}")]
    [SelfHosted(SelfHostedOnly = true)]
    [RequestSizeLimit(Constants.FileSize501mb)]
    [DisableFormValueModelBinding]
    public async Task PostFileForExistingSend(string id, string fileId)
    {
        if (!Request?.ContentType.Contains("multipart/") ?? true)
        {
            throw new BadRequestException("Invalid content.");
        }

        var send = await _sendRepository.GetByIdAsync(new Guid(id));
        await Request.GetFileAsync(async (stream) =>
        {
            await _nonAnonymousSendCommand.UploadFileToExistingSendAsync(stream, send);
        });
    }

    [HttpPut("{id}")]
    public async Task<SendResponseModel> Put(string id, [FromBody] SendRequestModel model)
    {
        model.ValidateEdit();
        var userId = _userService.GetProperUserId(User).Value;
        var send = await _sendRepository.GetByIdAsync(new Guid(id));
        if (send == null || send.UserId != userId)
        {
            throw new NotFoundException();
        }

        await _nonAnonymousSendCommand.SaveSendAsync(model.ToSend(send, _sendAuthorizationService));
        return new SendResponseModel(send, _globalSettings);
    }

    [HttpPut("{id}/remove-password")]
    public async Task<SendResponseModel> PutRemovePassword(string id)
    {
        var userId = _userService.GetProperUserId(User).Value;
        var send = await _sendRepository.GetByIdAsync(new Guid(id));
        if (send == null || send.UserId != userId)
        {
            throw new NotFoundException();
        }

        send.Password = null;
        await _nonAnonymousSendCommand.SaveSendAsync(send);
        return new SendResponseModel(send, _globalSettings);
    }

    [HttpDelete("{id}")]
    public async Task Delete(string id)
    {
        var userId = _userService.GetProperUserId(User).Value;
        var send = await _sendRepository.GetByIdAsync(new Guid(id));
        if (send == null || send.UserId != userId)
        {
            throw new NotFoundException();
        }

        await _nonAnonymousSendCommand.DeleteSendAsync(send);
    }

    #endregion
}
