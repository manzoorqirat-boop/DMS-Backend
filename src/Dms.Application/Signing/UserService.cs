using Dms.Application.Abstractions;
using Dms.Application.Common;
using Dms.Domain.Entities;
using Dms.Domain.Enums;

namespace Dms.Application.Signing;

/// <summary>
/// User account administration, and self-service password change.
/// <para>
/// Creation, listing and deactivation require <see cref="Permission.UserManage"/>.
/// <see cref="ChangeOwnPasswordAsync"/> does not — it is scoped to the caller's own account by
/// construction, since there is deliberately no administrator-reset path. A password an
/// administrator can set is a credential a second person knows, which would let them sign as
/// someone else — precisely what §11.200's distinct-credential requirement exists to prevent.
/// </para>
/// <para>
/// <b>Reconstructed file.</b> Present in the working codebase before this review but absent
/// from the uploaded archive. Rebuilt from <c>IUserRepository</c>, <c>UserRepository</c>,
/// <c>SigningDtos.cs</c> (<c>CreateUserRequest</c>, <c>UserSummary</c>) and the calling
/// convention used consistently by every other master-data service in this codebase (see
/// <c>DocumentTypeService</c>). The permission-gating and password-change design above follow
/// directly from those files and from constraints stated elsewhere in the codebase; anything
/// not fixed by that contract may differ from the original.
/// </para>
/// </summary>
public sealed class UserService(
    IUserRepository users,
    IAccessControl access,
    IAuditTrail audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string EntityType = "DmsUser";

    public async Task<Result<UserSummary>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await RequireUserManageAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        DmsUser user;
        try
        {
            user = new DmsUser(
                request.UserName, request.FullName, request.Department, request.Designation, request.Password);
        }
        catch (ArgumentException ex)
        {
            // The entity's own guards are the single source of truth for what a valid account
            // looks like; re-implementing them here would be a second definition to drift.
            return Error.Validation("user_invalid", ex.Message);
        }

        users.Add(user);
        audit.Record(
            AuditAction.UserCreated, EntityType, user.Id, user.UserName,
            $"{user.FullName}, {user.Designation}, {user.Department}.");

        var outcome = await users.SaveChangesAsync(cancellationToken);
        if (!outcome.Saved)
        {
            return outcome.ViolatedIndexContains("user_name")
                ? Error.Conflict("username_taken", $"A user named '{user.UserName}' already exists.")
                : Error.Conflict("user_save_conflict", "The user could not be saved.");
        }

        return UserSummary.From(user, clock.UtcNow);
    }

    public async Task<Result<IReadOnlyList<UserSummary>>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var gate = await RequireUserManageAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var found = await users.ListAsync(includeInactive, cancellationToken);
        var now = clock.UtcNow;

        return Result<IReadOnlyList<UserSummary>>.Success(
            found.Select(u => UserSummary.From(u, now)).ToList());
    }

    public async Task<Result<UserSummary>> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var gate = await RequireUserManageAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var user = await users.GetAsync(id, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", $"No user with id {id}.");
        }

        user.Deactivate();
        audit.Record(AuditAction.UserDeactivated, EntityType, user.Id, user.UserName);

        var outcome = await users.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? UserSummary.From(user, clock.UtcNow)
            : Error.Conflict("user_save_conflict", "The user could not be updated.");
    }

    public async Task<Result<UserSummary>> ReactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var gate = await RequireUserManageAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var user = await users.GetAsync(id, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", $"No user with id {id}.");
        }

        user.Reactivate();
        audit.Record(AuditAction.UserDeactivated, EntityType, user.Id, user.UserName, "Reactivated.");

        var outcome = await users.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? UserSummary.From(user, clock.UtcNow)
            : Error.Conflict("user_save_conflict", "The user could not be updated.");
    }

    /// <summary>
    /// Self-service only, and there is deliberately no administrator reset. Requires the
    /// current password even though the caller is already authenticated: the password is also
    /// the signing credential, so an unattended session must not be enough to take it over.
    /// </summary>
    public async Task<Result<bool>> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var user = await users.GetByUserNameAsync(currentUser.UserName, cancellationToken);
        if (user is null)
        {
            return Error.NotFound("user_not_found", "The acting user has no DMS account.");
        }

        if (!user.VerifyPassword(currentPassword))
        {
            audit.Record(
                AuditAction.UserPasswordChanged, EntityType, user.Id, user.UserName,
                "Change rejected: current password incorrect.");
            await users.SaveChangesAsync(cancellationToken);

            return Error.Validation("current_password_incorrect", "The current password is incorrect.");
        }

        try
        {
            user.ChangePassword(newPassword);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("password_invalid", ex.Message);
        }

        audit.Record(
            AuditAction.UserPasswordChanged, EntityType, user.Id, user.UserName,
            "Password changed by the account holder.");

        var outcome = await users.SaveChangesAsync(cancellationToken);
        return outcome.Saved
            ? Result<bool>.Success(true)
            : Error.Conflict("password_save_conflict", "The password could not be changed.");
    }

    private async Task<Error?> RequireUserManageAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentUser.UserName))
        {
            return Error.Validation("actor_unknown", "The acting user could not be determined.");
        }

        var allowed = await access.HasPermissionAsync(
            Permission.UserManage, siteId: null, departmentId: null, cancellationToken);

        return allowed
            ? null
            : Error.Validation(
                "permission_denied",
                $"{Permission.UserManage} at organisation-wide scope is required to manage user accounts.");
    }
}
