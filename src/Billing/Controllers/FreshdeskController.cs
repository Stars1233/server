﻿// FIXME: Update this file to be null safe and then delete the line below
#nullable disable

using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;
using Bit.Billing.Models;
using Bit.Core.Repositories;
using Bit.Core.Settings;
using Bit.Core.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bit.Billing.Controllers;

[Route("freshdesk")]
public class FreshdeskController : Controller
{
    private readonly BillingSettings _billingSettings;
    private readonly IUserRepository _userRepository;
    private readonly IOrganizationRepository _organizationRepository;
    private readonly ILogger<FreshdeskController> _logger;
    private readonly GlobalSettings _globalSettings;
    private readonly IHttpClientFactory _httpClientFactory;

    public FreshdeskController(
        IUserRepository userRepository,
        IOrganizationRepository organizationRepository,
        IOptions<BillingSettings> billingSettings,
        ILogger<FreshdeskController> logger,
        GlobalSettings globalSettings,
        IHttpClientFactory httpClientFactory)
    {
        _billingSettings = billingSettings?.Value;
        _userRepository = userRepository;
        _organizationRepository = organizationRepository;
        _logger = logger;
        _globalSettings = globalSettings;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> PostWebhook([FromQuery, Required] string key,
        [FromBody, Required] FreshdeskWebhookModel model)
    {
        if (string.IsNullOrWhiteSpace(key) || !CoreHelpers.FixedTimeEquals(key, _billingSettings.FreshDesk.WebhookKey))
        {
            return new BadRequestResult();
        }

        try
        {
            var ticketId = model.TicketId;
            var ticketContactEmail = model.TicketContactEmail;
            var ticketTags = model.TicketTags;
            if (string.IsNullOrWhiteSpace(ticketId) || string.IsNullOrWhiteSpace(ticketContactEmail))
            {
                return new BadRequestResult();
            }

            var updateBody = new Dictionary<string, object>();
            var note = string.Empty;
            note += $"<li>Region: {_billingSettings.FreshDesk.Region}</li>";
            var customFields = new Dictionary<string, object>();
            var user = await _userRepository.GetByEmailAsync(ticketContactEmail);
            if (user == null)
            {
                note += $"<li>No user found: {ticketContactEmail}</li>";
                await CreateNote(ticketId, note);
            }

            if (user != null)
            {
                var userLink = $"{_globalSettings.BaseServiceUri.Admin}/users/edit/{user.Id}";
                note += $"<li>User, {user.Email}: {userLink}</li>";
                customFields.Add(_billingSettings.FreshDesk.UserFieldName, userLink);
                var tags = new HashSet<string>();
                if (user.Premium)
                {
                    tags.Add("Premium");
                }
                var orgs = await _organizationRepository.GetManyByUserIdAsync(user.Id);

                foreach (var org in orgs)
                {
                    // Prevent org names from injecting any additional HTML
                    var orgName = HttpUtility.HtmlEncode(org.Name);
                    var orgNote = $"{orgName} ({org.Seats.GetValueOrDefault()}): " +
                        $"{_globalSettings.BaseServiceUri.Admin}/organizations/edit/{org.Id}";
                    note += $"<li>Org, {orgNote}</li>";
                    if (!customFields.Any(kvp => kvp.Key == _billingSettings.FreshDesk.OrgFieldName))
                    {
                        customFields.Add(_billingSettings.FreshDesk.OrgFieldName, orgNote);
                    }
                    else
                    {
                        customFields[_billingSettings.FreshDesk.OrgFieldName] += $"\n{orgNote}";
                    }

                    var planName = GetAttribute<DisplayAttribute>(org.PlanType).Name.Split(" ").FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(planName))
                    {
                        tags.Add(string.Format("Org: {0}", planName));
                    }
                }
                if (tags.Any())
                {
                    var tagsToUpdate = tags.ToList();
                    if (!string.IsNullOrWhiteSpace(ticketTags))
                    {
                        var splitTicketTags = ticketTags.Split(',');
                        for (var i = 0; i < splitTicketTags.Length; i++)
                        {
                            tagsToUpdate.Insert(i, splitTicketTags[i]);
                        }
                    }
                    updateBody.Add("tags", tagsToUpdate);
                }

                if (customFields.Any())
                {
                    updateBody.Add("custom_fields", customFields);
                }
                var updateRequest = new HttpRequestMessage(HttpMethod.Put,
                    string.Format("https://bitwarden.freshdesk.com/api/v2/tickets/{0}", ticketId))
                {
                    Content = JsonContent.Create(updateBody),
                };
                await CallFreshdeskApiAsync(updateRequest);
                await CreateNote(ticketId, note);
            }

            return new OkResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error processing freshdesk webhook.");
            return new BadRequestResult();
        }
    }

    [HttpPost("webhook-onyx-ai")]
    public async Task<IActionResult> PostWebhookOnyxAi([FromQuery, Required] string key,
        [FromBody, Required] FreshdeskOnyxAiWebhookModel model)
    {
        // ensure that the key is from Freshdesk
        if (!IsValidRequestFromFreshdesk(key))
        {
            return new BadRequestResult();
        }

        // create the onyx `answer-with-citation` request
        var onyxRequestModel = new OnyxAnswerWithCitationRequestModel(model.TicketDescriptionText, _billingSettings.Onyx.PersonaId);
        var onyxRequest = new HttpRequestMessage(HttpMethod.Post,
                            string.Format("{0}/query/answer-with-citation", _billingSettings.Onyx.BaseUrl))
        {
            Content = JsonContent.Create(onyxRequestModel, mediaType: new MediaTypeHeaderValue("application/json")),
        };
        var (_, onyxJsonResponse) = await CallOnyxApi<OnyxAnswerWithCitationResponseModel>(onyxRequest);

        // the CallOnyxApi will return a null if we have an error response
        if (onyxJsonResponse?.Answer == null || !string.IsNullOrEmpty(onyxJsonResponse?.ErrorMsg))
        {
            return BadRequest(
                string.Format("Failed to get a valid response from Onyx API. Response: {0}",
                            JsonSerializer.Serialize(onyxJsonResponse ?? new OnyxAnswerWithCitationResponseModel())));
        }

        // add the answer as a note to the ticket
        await AddAnswerNoteToTicketAsync(onyxJsonResponse.Answer, model.TicketId);

        return Ok();
    }

    private bool IsValidRequestFromFreshdesk(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
                || !CoreHelpers.FixedTimeEquals(key, _billingSettings.FreshDesk.WebhookKey))
        {
            return false;
        }

        return true;
    }

    private async Task CreateNote(string ticketId, string note)
    {
        var noteBody = new Dictionary<string, object>
                {
                    { "body", $"<ul>{note}</ul>" },
                    { "private", true }
                };
        var noteRequest = new HttpRequestMessage(HttpMethod.Post,
            string.Format("https://bitwarden.freshdesk.com/api/v2/tickets/{0}/notes", ticketId))
        {
            Content = JsonContent.Create(noteBody),
        };
        await CallFreshdeskApiAsync(noteRequest);
    }

    private async Task AddAnswerNoteToTicketAsync(string note, string ticketId)
    {
        // if there is no content, then we don't need to add a note
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var noteBody = new Dictionary<string, object>
                {
                    { "body", $"<b>Onyx AI:</b><ul>{note}</ul>" },
                    { "private", true }
                };

        var noteRequest = new HttpRequestMessage(HttpMethod.Post,
                    string.Format("https://bitwarden.freshdesk.com/api/v2/tickets/{0}/notes", ticketId))
        {
            Content = JsonContent.Create(noteBody),
        };

        var addNoteResponse = await CallFreshdeskApiAsync(noteRequest);
        if (addNoteResponse.StatusCode != System.Net.HttpStatusCode.Created)
        {
            _logger.LogError("Error adding note to Freshdesk ticket. Ticket Id: {0}. Status: {1}",
                            ticketId, addNoteResponse.ToString());
        }
    }

    private async Task<HttpResponseMessage> CallFreshdeskApiAsync(HttpRequestMessage request, int retriedCount = 0)
    {
        try
        {
            var freshdeskAuthkey = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_billingSettings.FreshDesk.ApiKey}:X"));
            var httpClient = _httpClientFactory.CreateClient("FreshdeskApi");
            request.Headers.Add("Authorization", $"Basic {freshdeskAuthkey}");
            var response = await httpClient.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || retriedCount > 3)
            {
                return response;
            }
        }
        catch
        {
            if (retriedCount > 3)
            {
                throw;
            }
        }
        await Task.Delay(30000 * (retriedCount + 1));
        return await CallFreshdeskApiAsync(request, retriedCount++);
    }

    private async Task<(HttpResponseMessage, T)> CallOnyxApi<T>(HttpRequestMessage request)
    {
        var httpClient = _httpClientFactory.CreateClient("OnyxApi");
        var response = await httpClient.SendAsync(request);

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            _logger.LogError("Error calling Onyx AI API. Status code: {0}. Response {1}",
                response.StatusCode, JsonSerializer.Serialize(response));
            return (null, default);
        }
        var responseStr = await response.Content.ReadAsStringAsync();
        var responseJson = JsonSerializer.Deserialize<T>(responseStr, options: new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return (response, responseJson);
    }

    private TAttribute GetAttribute<TAttribute>(Enum enumValue) where TAttribute : Attribute
    {
        return enumValue.GetType().GetMember(enumValue.ToString()).First().GetCustomAttribute<TAttribute>();
    }
}
