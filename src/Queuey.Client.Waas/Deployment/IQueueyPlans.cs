using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

// Et eget grensesnitt for planene Queuey lagrer (Queuey F3.11, KAN 1 fra reviewen av #64), som IQueueyManagement: et nytt medlem
// på IQueueyService, som er offentlig i en tagget versjon, ville brutt dem som implementerer det. AddQueuey registrerer det, og
// QueueyService implementerer begge.

/// <summary>
/// Configuration plans Queuey stores (Queuey F3.11): built from the dry runs of a deployment's writes, sealed with a hash and
/// the policy's decision, and approved in Queuey's inbox when the policy gives a plan to a person. An apply writes a plan with
/// <see cref="SyncOptions.Plan"/>. Registered by <c>AddQueuey</c>.
/// </summary>
public interface IQueueyPlans
{
    /// <summary>
    /// Builds a configuration plan Queuey stores: an empty plan with where the file lives (<see cref="SyncOptions.Source"/>)
    /// and what it takes back (<see cref="SyncOptions.Adopt"/>), every write apply would send as a dry run in it
    /// (<c>X-Queuey-Plan</c>), what apply sends to each queue it creates, then the seal: the hash and the policy's decision.
    /// With <paramref name="submit"/>, a plan the policy gives to a person goes to Queuey's inbox, and
    /// <see cref="StoredPlan.ApprovalUrl"/> says where; without it, the sealed plan waits to be submitted, which
    /// <see cref="SubmitStoredPlanAsync"/> does. Apply it with <see cref="SyncOptions.Plan"/> once
    /// <see cref="StoredPlan.CanBeApplied"/>.
    /// </summary>
    /// <returns>
    /// The plan with <see cref="DeploymentPlan.Stored"/> set, and <see cref="DeploymentPlan.PlanId"/> the stored plan's id. A
    /// plan with a refused write is not sealed, and its steps say why. Against a Queuey that stores no plans, the plan this
    /// client makes, without <see cref="DeploymentPlan.Stored"/>, and a line in <see cref="DeploymentPlan.Warnings"/> that says so.
    /// </returns>
    Task<DeploymentPlan> StorePlanAsync(DeploymentFile file, SyncOptions? options = null, bool submit = false, CancellationToken cancellationToken = default);

    /// <summary>Reads a stored plan, with its steps (<c>GET /tenants/{t}/deployment/plans/{plan}</c>).</summary>
    Task<StoredPlan> GetStoredPlanAsync(string planId, string? tenantPublicId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a sealed plan the policy gave to a person to Queuey's inbox, where it waits 24 hours for one. A plan already
    /// waiting answers the same again.
    /// </summary>
    Task<StoredPlan> SubmitStoredPlanAsync(string planId, string? tenantPublicId = null, CancellationToken cancellationToken = default);
}
