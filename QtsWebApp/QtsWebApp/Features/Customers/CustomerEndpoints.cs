using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Customers;
using QtsBilling.Contracts.Identity;
using QtsBilling.Persistence.Postgres;
using QtsBilling.Persistence.Postgres.Entities;
using QtsWebApp.Infrastructure;

namespace QtsWebApp.Features.Customers;

/// <summary>
/// Customer master data (QB-06). Every query is tenant-filtered from the authenticated principal, so a
/// foreign id is a 404 rather than a cross-business read/write (F-22, QB-04 IDOR tests).
/// </summary>
public static class CustomerEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;
    private const int MaxPhoneLength = 40;
    private const int MaxAddressLength = 500;

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customers").WithTags("Customers");

        group.MapGet("/", ListAsync).RequireAuthorization(Permissions.CustomersRead);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.CustomersWrite);
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(Permissions.CustomersWrite);
        group.MapDelete("/{id:guid}", DeactivateAsync).RequireAuthorization(Permissions.CustomersWrite);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        string? search,
        string? cursor,
        int? pageSize,
        bool? includeInactive,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Customers.AsNoTracking().Where(customer => customer.BusinessId == businessId);

        if (includeInactive != true)
        {
            query = query.Where(customer => customer.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search.Trim());
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, pattern)
                || (customer.Email != null && EF.Functions.ILike(customer.Email, pattern))
                || (customer.Phone != null && EF.Functions.ILike(customer.Phone, pattern)));
        }

        if (CursorPaging.TryDecode(cursor, out var cursorId))
        {
            query = query.Where(customer => customer.Id.CompareTo(cursorId) < 0);
        }

        // Identifiers are UUIDv7 (time-ordered), so id-order is a stable, unique page key.
        var rows = await query
            .OrderByDescending(customer => customer.Id)
            .Take(size + 1)
            .Select(customer => new CustomerDto(
                customer.Id,
                customer.Name,
                customer.Email,
                customer.Phone,
                customer.Address,
                customer.IsActive,
                customer.Version,
                customer.CreatedAtUtc,
                customer.UpdatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;

        if (rows.Count > size)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = CursorPaging.Encode(rows[^1].Id);
        }

        return Results.Ok(new PagedResult<CustomerDto>(rows, nextCursor, size));
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        BillingDbContext db,
        SaveCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var errors = Validate(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The customer payload is invalid.", errors);
        }

        var phoneNormalized = PhoneNormalizer.Normalize(request.Phone);

        if (phoneNormalized is not null
            && await db.Customers.AnyAsync(
                customer => customer.BusinessId == businessId && customer.PhoneNormalized == phoneNormalized,
                cancellationToken).ConfigureAwait(false))
        {
            return DuplicatePhone();
        }

        var customer = new Customer
        {
            BusinessId = businessId,
            Name = request.Name.Trim(),
            Email = NullIfBlank(request.Email),
            Phone = NullIfBlank(request.Phone),
            PhoneNormalized = phoneNormalized,
            Address = NullIfBlank(request.Address),
            IsActive = true,
        };

        db.Customers.Add(customer);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Lost the race against a concurrent create with the same normalized phone.
            return DuplicatePhone();
        }

        return Results.Created($"/api/v1/customers/{customer.Id}", ToDto(customer));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        HttpRequest httpRequest,
        SaveCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var customer = await db.Customers
            .FirstOrDefaultAsync(row => row.Id == id && row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return NotFound();
        }

        var errors = Validate(request);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationProblem("The customer payload is invalid.", errors);
        }

        if (httpRequest.Headers.TryGetValue("If-Match", out var ifMatch)
            && long.TryParse(ifMatch.ToString(), out var expectedVersion)
            && expectedVersion != customer.Version)
        {
            return ConcurrencyConflict();
        }

        var phoneNormalized = PhoneNormalizer.Normalize(request.Phone);

        if (phoneNormalized is not null
            && await db.Customers.AnyAsync(
                other => other.BusinessId == businessId && other.Id != id && other.PhoneNormalized == phoneNormalized,
                cancellationToken).ConfigureAwait(false))
        {
            return DuplicatePhone();
        }

        customer.Name = request.Name.Trim();
        customer.Email = NullIfBlank(request.Email);
        customer.Phone = NullIfBlank(request.Phone);
        customer.PhoneNormalized = phoneNormalized;
        customer.Address = NullIfBlank(request.Address);
        customer.IsActive = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return DuplicatePhone();
        }

        return Results.Ok(ToDto(customer));
    }

    /// <summary>
    /// Master data is deactivated, never erased: historical sales, invoices and journals must keep
    /// resolving the customer they were issued to (specification §3.7).
    /// </summary>
    private static async Task<IResult> DeactivateAsync(
        Guid id,
        ClaimsPrincipal principal,
        BillingDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.TryGetBusinessId(out var businessId))
        {
            return BusinessScope.MissingMembership();
        }

        var customer = await db.Customers
            .FirstOrDefaultAsync(row => row.Id == id && row.BusinessId == businessId, cancellationToken)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return NotFound();
        }

        if (customer.IsActive)
        {
            customer.IsActive = false;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    private static Dictionary<string, string[]> Validate(SaveCustomerRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            errors["name"] = ["A customer name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"The customer name must be at most {MaxNameLength} characters."];
        }

        var email = NullIfBlank(request.Email);

        if (email is not null && (email.Length > MaxEmailLength || !IsPlausibleEmail(email)))
        {
            errors["email"] = ["Enter a valid e-mail address (at most 320 characters)."];
        }

        var phone = NullIfBlank(request.Phone);

        if (phone is not null && phone.Length > MaxPhoneLength)
        {
            errors["phone"] = [$"The phone number must be at most {MaxPhoneLength} characters."];
        }

        var address = NullIfBlank(request.Address);

        if (address is not null && address.Length > MaxAddressLength)
        {
            errors["address"] = [$"The address must be at most {MaxAddressLength} characters."];
        }

        return errors;
    }

    private static bool IsPlausibleEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);

        return at > 0
            && at < email.Length - 1
            && email.IndexOf('.', at + 1) > at + 1
            && !email.Contains(' ', StringComparison.Ordinal);
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static CustomerDto ToDto(Customer customer)
        => new(
            customer.Id,
            customer.Name,
            customer.Email,
            customer.Phone,
            customer.Address,
            customer.IsActive,
            customer.Version,
            customer.CreatedAtUtc,
            customer.UpdatedAtUtc);

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static IResult DuplicatePhone()
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.DuplicatePhone,
            "Another customer in this business already uses this phone number.");

    private static IResult ConcurrencyConflict()
        => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            ApiErrorCodes.OptimisticConcurrencyFailure,
            "This customer was changed by someone else; reload the record and retry.");

    private static IResult NotFound()
        => ApiResults.Problem(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "The customer does not exist in this business.");
}
