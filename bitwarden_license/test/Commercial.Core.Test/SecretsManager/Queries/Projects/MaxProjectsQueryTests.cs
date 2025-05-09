﻿using System.Security.Claims;
using Bit.Commercial.Core.SecretsManager.Queries.Projects;
using Bit.Core.AdminConsole.Entities;
using Bit.Core.Billing.Enums;
using Bit.Core.Billing.Licenses;
using Bit.Core.Billing.Pricing;
using Bit.Core.Exceptions;
using Bit.Core.Models.Business;
using Bit.Core.Repositories;
using Bit.Core.SecretsManager.Repositories;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Utilities;
using Bit.Test.Common.AutoFixture;
using Bit.Test.Common.AutoFixture.Attributes;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Xunit;

namespace Bit.Commercial.Core.Test.SecretsManager.Queries.Projects;

[SutProviderCustomize]
public class MaxProjectsQueryTests
{
    [Theory]
    [BitAutoData]
    public async Task GetByOrgIdAsync_OrganizationIsNull_ThrowsNotFound(SutProvider<MaxProjectsQuery> sutProvider,
        Guid organizationId)
    {
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(default).ReturnsNull();

        await Assert.ThrowsAsync<NotFoundException>(async () => await sutProvider.Sut.GetByOrgIdAsync(organizationId, 1));

        await sutProvider.GetDependency<IProjectRepository>().DidNotReceiveWithAnyArgs()
            .GetProjectCountByOrganizationIdAsync(organizationId);
    }

    [Theory]
    [BitAutoData(PlanType.FamiliesAnnually2019)]
    [BitAutoData(PlanType.Custom)]
    [BitAutoData(PlanType.FamiliesAnnually)]
    public async Task GetByOrgIdAsync_Cloud_SmPlanIsNull_ThrowsBadRequest(PlanType planType,
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        organization.PlanType = planType;
        sutProvider.GetDependency<IOrganizationRepository>()
            .GetByIdAsync(organization.Id)
            .Returns(organization);

        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(false);
        var plan = StaticStore.GetPlan(planType);
        sutProvider.GetDependency<IPricingClient>().GetPlan(organization.PlanType).Returns(plan);

        await Assert.ThrowsAsync<BadRequestException>(
            async () => await sutProvider.Sut.GetByOrgIdAsync(organization.Id, 1));

        await sutProvider.GetDependency<IProjectRepository>()
            .DidNotReceiveWithAnyArgs()
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }

    [Theory]
    [BitAutoData]
    public async Task GetByOrgIdAsync_SelfHosted_NoMaxProjectsClaim_ThrowsBadRequest(
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        sutProvider.GetDependency<IOrganizationRepository>()
            .GetByIdAsync(organization.Id)
            .Returns(organization);

        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(true);

        var license = new OrganizationLicense();
        var claimsPrincipal = new ClaimsPrincipal();
        sutProvider.GetDependency<ILicensingService>().ReadOrganizationLicenseAsync(organization).Returns(license);
        sutProvider.GetDependency<ILicensingService>().GetClaimsPrincipalFromLicense(license).Returns(claimsPrincipal);

        await Assert.ThrowsAsync<BadRequestException>(
            async () => await sutProvider.Sut.GetByOrgIdAsync(organization.Id, 1));

        await sutProvider.GetDependency<IProjectRepository>()
            .DidNotReceiveWithAnyArgs()
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsMonthly2019)]
    [BitAutoData(PlanType.TeamsMonthly2020)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.TeamsAnnually2019)]
    [BitAutoData(PlanType.TeamsAnnually2020)]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsStarter)]
    [BitAutoData(PlanType.EnterpriseMonthly2019)]
    [BitAutoData(PlanType.EnterpriseMonthly2020)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    [BitAutoData(PlanType.EnterpriseAnnually2019)]
    [BitAutoData(PlanType.EnterpriseAnnually2020)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    public async Task GetByOrgIdAsync_Cloud_SmNoneFreePlans_ReturnsNull(PlanType planType,
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        organization.PlanType = planType;
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);

        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(false);
        var plan = StaticStore.GetPlan(planType);
        sutProvider.GetDependency<IPricingClient>().GetPlan(organization.PlanType).Returns(plan);

        var (limit, overLimit) = await sutProvider.Sut.GetByOrgIdAsync(organization.Id, 1);

        Assert.Null(limit);
        Assert.Null(overLimit);

        await sutProvider.GetDependency<IProjectRepository>().DidNotReceiveWithAnyArgs()
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsMonthly2019)]
    [BitAutoData(PlanType.TeamsMonthly2020)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.TeamsAnnually2019)]
    [BitAutoData(PlanType.TeamsAnnually2020)]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsStarter)]
    [BitAutoData(PlanType.EnterpriseMonthly2019)]
    [BitAutoData(PlanType.EnterpriseMonthly2020)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    [BitAutoData(PlanType.EnterpriseAnnually2019)]
    [BitAutoData(PlanType.EnterpriseAnnually2020)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    public async Task GetByOrgIdAsync_SelfHosted_SmNoneFreePlans_ReturnsNull(PlanType planType,
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        organization.PlanType = planType;
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(true);

        var license = new OrganizationLicense();
        var plan = StaticStore.GetPlan(planType);
        var claims = new List<Claim>
        {
            new (nameof(OrganizationLicenseConstants.PlanType), organization.PlanType.ToString()),
            new (nameof(OrganizationLicenseConstants.SmMaxProjects), plan.SecretsManager.MaxProjects.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuthenticationType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        sutProvider.GetDependency<ILicensingService>().ReadOrganizationLicenseAsync(organization).Returns(license);
        sutProvider.GetDependency<ILicensingService>().GetClaimsPrincipalFromLicense(license).Returns(claimsPrincipal);

        var (limit, overLimit) = await sutProvider.Sut.GetByOrgIdAsync(organization.Id, 1);

        Assert.Null(limit);
        Assert.Null(overLimit);

        await sutProvider.GetDependency<IProjectRepository>().DidNotReceiveWithAnyArgs()
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }

    [Theory]
    [BitAutoData(PlanType.Free, 0, 1, false)]
    [BitAutoData(PlanType.Free, 1, 1, false)]
    [BitAutoData(PlanType.Free, 2, 1, false)]
    [BitAutoData(PlanType.Free, 3, 1, true)]
    [BitAutoData(PlanType.Free, 4, 1, true)]
    [BitAutoData(PlanType.Free, 40, 1, true)]
    [BitAutoData(PlanType.Free, 0, 2, false)]
    [BitAutoData(PlanType.Free, 1, 2, false)]
    [BitAutoData(PlanType.Free, 2, 2, true)]
    [BitAutoData(PlanType.Free, 3, 2, true)]
    [BitAutoData(PlanType.Free, 4, 2, true)]
    [BitAutoData(PlanType.Free, 40, 2, true)]
    [BitAutoData(PlanType.Free, 0, 3, false)]
    [BitAutoData(PlanType.Free, 1, 3, true)]
    [BitAutoData(PlanType.Free, 2, 3, true)]
    [BitAutoData(PlanType.Free, 3, 3, true)]
    [BitAutoData(PlanType.Free, 4, 3, true)]
    [BitAutoData(PlanType.Free, 40, 3, true)]
    [BitAutoData(PlanType.Free, 0, 4, true)]
    [BitAutoData(PlanType.Free, 1, 4, true)]
    [BitAutoData(PlanType.Free, 2, 4, true)]
    [BitAutoData(PlanType.Free, 3, 4, true)]
    [BitAutoData(PlanType.Free, 4, 4, true)]
    [BitAutoData(PlanType.Free, 40, 4, true)]
    public async Task GetByOrgIdAsync_Cloud_SmFreePlan__Success(PlanType planType, int projects, int projectsToAdd, bool expectedOverMax,
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        organization.PlanType = planType;
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IProjectRepository>().GetProjectCountByOrganizationIdAsync(organization.Id)
            .Returns(projects);

        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(false);
        var plan = StaticStore.GetPlan(planType);
        sutProvider.GetDependency<IPricingClient>().GetPlan(organization.PlanType).Returns(plan);

        var (max, overMax) = await sutProvider.Sut.GetByOrgIdAsync(organization.Id, projectsToAdd);

        Assert.NotNull(max);
        Assert.NotNull(overMax);
        Assert.Equal(3, max.Value);
        Assert.Equal(expectedOverMax, overMax);

        await sutProvider.GetDependency<IProjectRepository>().Received(1)
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }

    [Theory]
    [BitAutoData(PlanType.Free, 0, 1, false)]
    [BitAutoData(PlanType.Free, 1, 1, false)]
    [BitAutoData(PlanType.Free, 2, 1, false)]
    [BitAutoData(PlanType.Free, 3, 1, true)]
    [BitAutoData(PlanType.Free, 4, 1, true)]
    [BitAutoData(PlanType.Free, 40, 1, true)]
    [BitAutoData(PlanType.Free, 0, 2, false)]
    [BitAutoData(PlanType.Free, 1, 2, false)]
    [BitAutoData(PlanType.Free, 2, 2, true)]
    [BitAutoData(PlanType.Free, 3, 2, true)]
    [BitAutoData(PlanType.Free, 4, 2, true)]
    [BitAutoData(PlanType.Free, 40, 2, true)]
    [BitAutoData(PlanType.Free, 0, 3, false)]
    [BitAutoData(PlanType.Free, 1, 3, true)]
    [BitAutoData(PlanType.Free, 2, 3, true)]
    [BitAutoData(PlanType.Free, 3, 3, true)]
    [BitAutoData(PlanType.Free, 4, 3, true)]
    [BitAutoData(PlanType.Free, 40, 3, true)]
    [BitAutoData(PlanType.Free, 0, 4, true)]
    [BitAutoData(PlanType.Free, 1, 4, true)]
    [BitAutoData(PlanType.Free, 2, 4, true)]
    [BitAutoData(PlanType.Free, 3, 4, true)]
    [BitAutoData(PlanType.Free, 4, 4, true)]
    [BitAutoData(PlanType.Free, 40, 4, true)]
    public async Task GetByOrgIdAsync_SelfHosted_SmFreePlan__Success(PlanType planType, int projects, int projectsToAdd, bool expectedOverMax,
        SutProvider<MaxProjectsQuery> sutProvider, Organization organization)
    {
        organization.PlanType = planType;
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IProjectRepository>().GetProjectCountByOrganizationIdAsync(organization.Id)
            .Returns(projects);
        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(true);

        var license = new OrganizationLicense();
        var plan = StaticStore.GetPlan(planType);
        var claims = new List<Claim>
        {
            new (nameof(OrganizationLicenseConstants.PlanType), organization.PlanType.ToString()),
            new (nameof(OrganizationLicenseConstants.SmMaxProjects), plan.SecretsManager.MaxProjects.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuthenticationType");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        sutProvider.GetDependency<ILicensingService>().ReadOrganizationLicenseAsync(organization).Returns(license);
        sutProvider.GetDependency<ILicensingService>().GetClaimsPrincipalFromLicense(license).Returns(claimsPrincipal);

        var (max, overMax) = await sutProvider.Sut.GetByOrgIdAsync(organization.Id, projectsToAdd);

        Assert.NotNull(max);
        Assert.NotNull(overMax);
        Assert.Equal(3, max.Value);
        Assert.Equal(expectedOverMax, overMax);

        await sutProvider.GetDependency<IProjectRepository>().Received(1)
            .GetProjectCountByOrganizationIdAsync(organization.Id);
    }
}
